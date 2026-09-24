package org.openaudiolink.phone

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import org.openaudiolink.core.Announce
import org.openaudiolink.core.Discovery
import org.openaudiolink.core.DiscoveryClient
import org.openaudiolink.core.NodeClient
import org.openaudiolink.core.NowPlaying
import org.openaudiolink.core.PcmRing
import org.openaudiolink.core.Rtp
import org.openaudiolink.core.RtpSender
import org.openaudiolink.core.Station
import org.openaudiolink.core.Track
import org.openaudiolink.phone.sources.AudioSource
import org.openaudiolink.phone.sources.LibrarySource
import org.openaudiolink.phone.sources.SpotifyAccount
import org.openaudiolink.phone.sources.SpotifySource
import java.net.InetAddress

/**
 * What the app knows and what it can be told to do.
 *
 * One object rather than a graph of them, because this app is small on
 * purpose: decision 19 gives the phone a Producer and just enough
 * Controller to get a stream running, and the moment this needs a
 * dependency framework it has stopped being that.
 *
 * The service owns the lifecycle; the UI only reads [state] and calls the
 * commands below.
 */
object Producer {

    private const val TAG = "oal.producer"

    /**
     * One device on the network, and what this phone may do with it.
     *
     * Three separate questions, because conflating them is what put a
     * Windows PC in the speaker list: whether it can *play* what this
     * phone sends, whether it can be *told to play* something of its own,
     * and whether it is merely present.
     */
    data class Speaker(
        val id: String,
        val name: String,
        val address: String,
        val controlPort: Int,
        val canReceiveAudio: Boolean = false,
        val canBeToldToPlay: Boolean = false,
        val selected: Boolean = false,
        val volume: Int = -1,
        val roomCorrection: Boolean = false,
        /*
         * Whether the node has actually answered.
         *
         * Without it, "we have not asked yet" and "this firmware has no
         * volume control" look identical, and the app says the second when
         * it means the first — which is what it told a person about three
         * healthy devices while every request was being refused before it
         * left the phone.
         */
        val answered: Boolean = false,
        /**
         * What firmware it is running, straight from its announce.
         *
         * Carried rather than derived, and shown on the card, because the
         * failure it guards against is silent: `protocol/OTA.md` records
         * that installing an image carrying the version already running
         * completes normally, reboots, and leaves the device reporting
         * exactly what it reported before — indistinguishable from an
         * update that did nothing, unless the version is on screen.
         *
         * It has been in every announce since the protocol had a `fw`
         * field. It was simply thrown away here.
         */
        val fw: String = "",
        /**
         * Whether anything has been heard from it lately.
         *
         * A ticked speaker that goes quiet stays on the list, marked, and
         * keeps receiving audio. Removing it was the old behaviour and it
         * was wrong twice over: the tick went with it, so the device came
         * back unticked and silent, and the audio stopped for a device
         * that was very often still there and simply not being heard.
         */
        val online: Boolean = true,
    ) {
        val hasVolume: Boolean get() = volume >= 0
        val volumeUnsupported: Boolean get() = answered && volume < 0
        val unreachable: Boolean get() = !answered
    }

    data class State(
        val speakers: List<Speaker> = emptyList(),
        val streaming: Boolean = false,
        val sourceLabel: String? = null,
        /**
         * The last few things the source itself said, most recent last.
         *
         * The packet counters below prove this *app* is sending; they say
         * nothing about whether the source is happy. For Spotify that is
         * the entire question — librespot can run, publish nothing, and
         * fail to authenticate, and from outside that is indistinguishable
         * from a cast point Spotify has simply not listed yet.
         *
         * A list rather than a line, because showing only the last one
         * produced a screenshot of librespot's stop handler complaining
         * about a missing context: true, and downstream of whatever
         * actually went wrong.
         */
        val sourceLog: List<String> = emptyList(),
        /**
         * Whether the source is in a state that can play at all.
         *
         * Null where the question does not apply. For Spotify it is
         * whether librespot authenticated, which is the fork everything
         * else depends on: not authenticated and nothing else matters,
         * authenticated and still silent is a different investigation.
         */
        val sourceReady: Boolean? = null,
        /**
         * What is playing, where the source can say.
         *
         * Null for a test tone, a vinyl node, a local file and any radio
         * station that sends no metadata — all of which is normal rather
         * than a failure, so the banner falls back to the source label
         * instead of showing an apology.
         */
        val nowPlaying: NowPlaying? = null,
        /**
         * Whether audio is actually leaving the phone.
         *
         * Separate from [streaming], and the separation is the point. A
         * published Spotify cast point that nobody has selected produces
         * no audio, so the source runs, the sender runs, and the wire
         * stays empty until somebody in Spotify presses play. Reporting
         * those two states as one made pressing *Publish* look like it
         * had started playing something.
         */
        val sendingAudio: Boolean = false,
        /** Packets not sent because there was nothing to play. */
        val packetsHeld: Long = 0,
        /**
         * This end's own view of how evenly it sent.
         *
         * The number to read beside a node's `arrivalGaps`. Both agreeing
         * means this phone stalled; the node alone means the network
         * clumped the packets on the way, which nothing here can fix.
         */
        val sendGaps: Long = 0,
        val sendGapWorstMs: Long = 0,
        val sendGapShape: List<Long> = emptyList(),
        val packetsSent: Long = 0,
        val underruns: Long = 0,
        val resyncs: Long = 0,
        /**
         * Whether the search for speakers is actually running.
         *
         * "None found yet" and "never started looking" are different
         * situations and only one of them is worth waiting through.
         */
        val discovering: Boolean = false,
        /*
         * What the radio is actually doing, on screen.
         *
         * An empty list is the same picture whether the socket is dead,
         * the interface is wrong, or the room simply has no speakers in
         * it. A count that moves separates the first from the last, and
         * the interface name separates the middle one.
         */
        val listeningOn: String? = null,
        /**
         * This phone's own address, beside the interface it listens on.
         *
         * Cheap, and it answers a question nothing else on the screen can:
         * whether the phone and the speakers are on the same subnet. Split
         * the house across two bands — speakers on 2.4 GHz, phone on 5 —
         * and a router that bridges them is invisible while a router that
         * routes them is fatal. In the second case discovery finds
         * nothing, which reads identically to a quiet network, a wrong
         * interface, or no speakers switched on.
         *
         * Two addresses in the same /24 as the speakers' means bridged.
         * Different ones mean the bands are separate networks and no
         * amount of waiting will help.
         */
        val localAddress: String? = null,
        val datagramsHeard: Long = 0,
        val announcesSent: Long = 0,
        val probesSent: Long = 0,
        val discoveryError: String? = null,
        /** Whether this device is mounted rather than carried. See Prefs. */
        val wallPanel: Boolean = false,
        /** Whether the cast point has ever been signed in. */
        val spotifySignedIn: Boolean = false,
        /**
         * The three states Spotify actually has, as two flags plus the
         * sign-in above.
         *
         * *No account* — [spotifySignedIn] false. Nothing is published and
         * nothing can be. *Available* — signed in and [castPointUp]: the
         * device is in everybody's Spotify, waiting, costing nothing.
         * *Playing* — [castPointCasting]: somebody has picked it and audio
         * is arriving.
         *
         * Worth keeping apart from [streaming], which answers a different
         * question: whether *this app* is sending RTP. A cast point can be
         * up while the radio is playing, and that is the arrangement the
         * whole sticky-cast-point design exists to allow.
         */
        val castPointUp: Boolean = false,
        /** Whether somebody is casting to it right now. */
        val castPointCasting: Boolean = false,
        /** The sticky switch — see [Prefs.castPoint]. */
        val castPointOn: Boolean = true,
        /** Set while the one-time sign-in is waiting for the browser. */
        val signingIn: Boolean = false,
        /** Something a person needs to be told, in their own words. */
        val warning: String? = null,
        /** What this phone calls itself, on the network and in Spotify. */
        val castName: String = "",
        /** Saved radio stations, in the order they were added. */
        val stations: List<Station> = emptyList(),
        /** Files picked before, newest first. See [Track]. */
        val tracks: List<Track> = emptyList(),
        /** Whether the counters and logs are on screen — see [Prefs]. */
        val showDetails: Boolean = false,
    ) {
        val selected: List<Speaker> get() = speakers.filter { it.selected }

        /** Speakers: things that can play what this phone sends. */
        val destinations: List<Speaker> get() = speakers.filter { it.canReceiveAudio }

        /** Other sources on the network — a turntable node, not the Hub. */
        val sources: List<Speaker> get() =
            speakers.filter { it.canBeToldToPlay && !it.canReceiveAudio }

        /**
         * Present, but nothing this app drives — the Hub, most obviously.
         *
         * Listed rather than hidden. A person who can see the Hub in the
         * app knows the network is right and this phone simply does not
         * command it; a person who cannot see it wonders whether discovery
         * is broken.
         */
        val others: List<Speaker> get() =
            speakers.filter { !it.canReceiveAudio && !it.canBeToldToPlay }
    }

    private val _state = MutableStateFlow(State())
    val state: StateFlow<State> = _state.asStateFlow()

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    /**
     * The speakers this phone has been told to play on, by id.
     *
     * **Held here rather than derived from the visible list, and that is
     * the fix for the bug this exists to record.** `refreshSpeakers` used
     * to read the ticks off the speakers it already had and re-apply them
     * to the ones it could currently see. A device that dropped out of the
     * liveness window vanished from that list, and with it the only record
     * that anybody had chosen it — so "Look again" brought it back
     * unticked, every time, and a party went quiet until somebody noticed
     * and re-ticked three speakers by hand.
     *
     * A choice is about a device, not about whether a datagram arrived in
     * the last thirty seconds.
     */
    private val selectedIds = LinkedHashSet<String>()

    private val ring = PcmRing(capacityFrames = Rtp.SAMPLE_RATE)   // one second
    private var sender: RtpSender? = null
    private var discovery: DiscoveryClient? = null
    private var binding: WifiBinding? = null
    private var source: AudioSource? = null

    /**
     * librespot, running on its own clock rather than the stream's.
     *
     * Held here, apart from [source], and that separation is the whole of
     * the sticky cast point. Everything else in this app is a source that
     * exists while it plays; this is a service that exists while the app
     * does, and is *sometimes* also the source.
     *
     * So it survives `startStream` replacing the source, it survives
     * `stopStream`, and the only things that end it are the switch being
     * turned off, the account being forgotten, and the app going away.
     */
    private var castPoint: SpotifySource? = null

    /**
     * Until when an arriving cast is ignored.
     *
     * A guard against the two ends fighting. Somebody at the panel picks
     * radio, which ejects whoever was casting; their phone may then
     * transfer straight back onto the device as it reappears, which would
     * take the radio away again — and round once more. A few seconds of
     * deafness after a deliberate choice at the panel settles it in
     * favour of the person standing in the room.
     */
    private var ignoreCastsUntil = 0L

    /** Kept so a change of selection can be written down without a screen. */
    private var appContext: Context? = null

    /* ---------- lifecycle, called by the service ---------- */

    /**
     * Brings the producer up. Safe to call from the main thread.
     *
     * The locks and the Wi-Fi look-up are ordinary system calls and happen
     * here, so `binding` is set before this returns and a person pressing
     * play immediately is not told there is no Wi-Fi when there is.
     *
     * **Everything that touches a socket is deferred**, and that is not a
     * tidiness preference. Joining a multicast group and sending a probe
     * are network operations, Android throws NetworkOnMainThreadException
     * for those on the main thread, and this runs from a service's
     * onCreate — so the exception killed the process before the first
     * frame was drawn, the service being START_STICKY brought it back, and
     * it died again. From outside that is an app that crashes on launch
     * and never shows a screen.
     */
    fun attach(context: Context, identity: Announce) {
        appContext = context.applicationContext
        val wifi = WifiBinding(context).also { binding = it }
        wifi.acquireLocks()
        readSettings(context)

        /*
         * The ticks, restored before anything has been heard from.
         *
         * So a phone that was restarted — or an app the system reclaimed
         * mid-party — comes back already pointed at the same speakers, and
         * they start playing as they announce themselves rather than
         * waiting for somebody to tick three boxes again.
         */
        synchronized(selectedIds) {
            selectedIds.clear()
            selectedIds.addAll(Prefs.selected(context))
        }
        readSpotifyState(context)

        /*
         * The cast point comes up with the app, not with the stream.
         *
         * This one line is the difference between a device that is in
         * everybody's Spotify all evening and one that has to be asked
         * for first.
         */
        refreshCastPoint(context)
        watchCastPoint()

        if (wifi.wifiNetwork() == null) {
            warn("No Wi-Fi. The speakers are on the Wi-Fi, so nothing can reach them yet.")
        }

        scope.launch {
            try {
                val client = DiscoveryClient(
                    self = identity,
                    networkInterface = wifi.multicastInterface(),
                    // Pinned to the Wi-Fi, for the same reason the audio
                    // socket is: a phone with mobile data up is multi-homed.
                    bindSocket = { wifi.bindToWifi(it) },
                )
                client.onChange = { refreshSpeakers() }
                /*
                 * Assigned before start(), and that ordering is a bug fix.
                 *
                 * The listener threads begin inside start(), so an announce
                 * could arrive while `discovery` was still null —
                 * refreshSpeakers() would bail, and because the peer table
                 * only reports *changes*, that speaker would never fire the
                 * callback again. One unlucky millisecond and a speaker
                 * stayed invisible for the life of the app.
                 */
                discovery = client
                client.start()
                _state.update {
                    it.copy(
                        discovering = true,
                        listeningOn = client.joinedOn,
                        localAddress = wifi.localAddress(),
                    )
                }
                client.probe()
            } catch (e: Exception) {
                /*
                 * A caught failure a person can read, rather than a dead
                 * process. Discovery not starting means no speakers appear,
                 * which is worth saying; it is not worth taking the app
                 * down for, because every other control still works.
                 */
                Log.e(TAG, "discovery did not start", e)
                warn("Could not start looking for speakers: ${e.message}")
            }

            /*
             * And poll, rather than trusting the callback alone.
             *
             * Announces arrive every five seconds and the callback fires
             * only on a change, so any single missed edge leaves the list
             * wrong until something else happens to change. A two-second
             * read of a table already held in memory costs nothing and
             * means the screen cannot get stuck showing "no speakers"
             * while three of them are announcing.
             */
            while (isActive) {
                refreshSpeakers()
                probeIfQuiet()
                readCounters()
                delay(2_000)
            }
        }
    }

    fun detach() {
        stopStream()
        castPoint?.shutDown()
        castPoint = null
        discovery?.stop()
        discovery = null
        binding?.releaseLocks()
        binding = null
    }

    /* ---------- the stream ---------- */

    fun startStream(newSource: AudioSource) {
        val wifi = binding ?: return

        /*
         * Started with nobody ticked, and that is allowed.
         *
         * An earlier version refused, to stop a stream that runs perfectly
         * and is heard by no one — the pacer paces, the packets are built,
         * and every one is addressed to an empty list. But refusing was the
         * wrong cure for that. Publishing a Spotify cast point before any
         * speaker exists is a real thing to want: it is how a party starts,
         * and destinations can be added mid-stream without the speakers
         * already playing noticing. So it runs, and the screen says loudly
         * that nothing is being heard.
         */
        if (_state.value.selected.isEmpty()) {
            warn("Nothing is ticked, so this plays to nobody — tick a speaker when one appears.")
        }

        /*
         * A deliberate choice at the panel outranks a remote cast, and
         * whoever was casting is ejected rather than left playing into
         * nothing. Not for Spotify itself, obviously — that would restart
         * the very session being selected.
         */
        if (newSource !== castPoint) {
            ignoreCastsUntil = System.currentTimeMillis() + CAST_GRACE_MS
            ejectCast("${newSource.label} was chosen at the panel")
        }

        stopStream()
        ring.clear()
        // Counted per stream, like packetsSent, or the two are on
        // different clocks and the screen compares them anyway.
        ring.resetCounters()

        val rtp = RtpSender(
            ring,
            /*
             * The scheduler priority that actually does something.
             *
             * RtpSender sets Thread.MAX_PRIORITY, which on Android is very
             * nearly a no-op: the Java priorities are squeezed into a
             * narrow band of nice values. THREAD_PRIORITY_URGENT_AUDIO is
             * nice -19, the band the platform's own audio threads run in,
             * and it is the difference between a 5 ms loop that holds and
             * one the scheduler feels free to park behind a UI frame.
             */
            onSendingThread = {
                try {
                    android.os.Process.setThreadPriority(
                        android.os.Process.THREAD_PRIORITY_URGENT_AUDIO
                    )
                } catch (e: Exception) {
                    // A device that refuses the priority still plays music.
                    Log.w(TAG, "could not raise the sending thread's priority", e)
                }
            },
            socketProvider = {
                /*
                 * Bound to the Wi-Fi where there is one, unbound where
                 * there is not — never refused.
                 *
                 * A phone *hosting* the hotspot has no station network at
                 * all, so `wifiNetwork()` is null and there is nothing to
                 * bind to; that is the party-mode arrangement of decision
                 * 20, not a fault. Refusing to open a socket there would
                 * have made the one deployment this app exists for the one
                 * it cannot do.
                 */
                wifi.boundSocket() ?: java.net.DatagramSocket().also { wifi.expediteAudio(it) }
            },
        )
        rtp.setDestinations(currentDestinations())
        rtp.start()
        sender = rtp

        newSource.start(ring)
        source = newSource

        _state.update {
            it.copy(
                streaming = true,
                sourceLabel = newSource.label,
                sourceLog = emptyList(),
                sourceReady = null,
                nowPlaying = null,
                sendingAudio = false,
                packetsSent = 0,
                packetsHeld = 0,
                warning = null,
            )
        }
        pollCounters()
    }

    fun stopStream() {
        source?.stop()
        source = null
        sender?.stop()
        sender = null
        _state.update {
            it.copy(
                streaming = false,
                sourceLabel = null,
                sourceLog = emptyList(),
                sourceReady = null,
                nowPlaying = null,
                sendingAudio = false,
            )
        }
    }

    /* ---------- the four controls ---------- */

    /**
     * Adding or removing a speaker mid-song.
     *
     * On the phone's own stream this is a change to the destination list
     * and nothing else — no teardown, no restart, and the speakers already
     * playing never know. A late joiner starts wherever the stream has
     * reached and its probation handles arriving mid-stream.
     */
    fun toggleSpeaker(id: String) {
        val nowSelected = synchronized(selectedIds) {
            if (!selectedIds.remove(id)) {
                selectedIds.add(id)
                true
            } else {
                false
            }
        }
        appContext?.let { Prefs.setSelected(it, selectedIds.toList()) }
        _state.update { current ->
            current.copy(speakers = current.speakers.map {
                if (it.id == id) it.copy(selected = nowSelected) else it
            })
        }
        sender?.setDestinations(currentDestinations())
    }

    /**
     * Volume is per node, not per source.
     *
     * A Consumer property, and it stays one — decision 14. An app that
     * wants Spotify quieter than the record player remembers a set of node
     * volumes per source and applies it on selection; that lives here, and
     * costs the firmware nothing.
     */
    fun setVolume(id: String, percent: Int) {
        val speaker = _state.value.speakers.firstOrNull { it.id == id } ?: return
        _state.update { current ->
            current.copy(speakers = current.speakers.map {
                if (it.id == id) it.copy(volume = percent) else it
            })
        }
        scope.launch { client(speaker).setVolume(percent) }
    }

    /** Room correction, on the speaker that holds it. It travels in NVS. */
    fun setRoomCorrection(id: String, enabled: Boolean) {
        val speaker = _state.value.speakers.firstOrNull { it.id == id } ?: return
        _state.update { current ->
            current.copy(speakers = current.speakers.map {
                if (it.id == id) it.copy(roomCorrection = enabled) else it
            })
        }
        scope.launch { client(speaker).setRoomCorrection(enabled) }
    }

    /**
     * The turntable: the phone is its Controller, not its source.
     *
     * The vinyl node is already a Producer, so the phone tells it where to
     * send rather than carrying the audio. From the app's point of view it
     * is the same operation as its own stream, which is why they share a
     * screen.
     */
    fun startVinyl(nodeId: String) {
        val node = _state.value.speakers.firstOrNull { it.id == nodeId } ?: return
        val destinations = currentDestinations().map { it.hostAddress ?: "" }.filter { it.isNotEmpty() }
        if (destinations.isEmpty()) {
            warn("Choose at least one speaker first.")
            return
        }
        scope.launch { client(node).startStream(destinations) }
    }

    fun stopVinyl(nodeId: String) {
        val node = _state.value.speakers.firstOrNull { it.id == nodeId } ?: return
        scope.launch { client(node).stopStream() }
    }

    /** "Look again". Off the main thread, because a probe is a send. */
    fun probe() {
        val client = discovery ?: return
        scope.launch {
            try {
                client.probe()
            } catch (e: Exception) {
                Log.w(TAG, "probe failed", e)
            }
            // Straight away, so the press has a visible effect rather than
            // waiting up to two seconds for the poll to notice.
            readCounters()
            refreshSpeakers()
        }
    }

    fun dismissWarning() = _state.update { it.copy(warning = null) }

    /* ---------- internals ---------- */

    /** Copies the discovery counters into the state the screen reads. */
    private fun readCounters() {
        val client = discovery ?: return
        _state.update {
            it.copy(
                listeningOn = client.joinedOn,
                localAddress = binding?.localAddress() ?: it.localAddress,
                datagramsHeard = client.datagramsHeard,
                announcesSent = client.announcesSent,
                probesSent = client.probesSent,
                discoveryError = client.lastError,
            )
        }
    }

    private fun client(speaker: Speaker) = NodeClient(speaker.address, speaker.controlPort)

    private fun currentDestinations(): List<InetAddress> =
        _state.value.selected.mapNotNull {
            try {
                InetAddress.getByName(it.address)
            } catch (_: Exception) {
                null
            }
        }

    private fun warn(message: String) = _state.update { it.copy(warning = message) }

    /**
     * Rebuilds the speaker list, keeping what the person chose.
     *
     * "Keeping" now means keeping the device, not just the tick. A ticked
     * speaker that has gone quiet stays on the list marked offline and
     * stays in the destination set, because the alternative — the old
     * behaviour — was to drop it, forget it had been chosen, stop sending
     * to it, and then greet it as a stranger when its next announce
     * happened to survive the air.
     *
     * Multicast on Wi-Fi is the least reliable thing on the network. A
     * speaker missing six announces in a row is thirty seconds and is
     * usually a run of bad luck rather than a device that left the house,
     * and continuing to send to it costs one unicast stream to an address
     * that either answers or does not.
     */
    private fun refreshSpeakers() {
        val table = discovery?.peers ?: return
        val now = System.currentTimeMillis()
        val chosen = synchronized(selectedIds) { selectedIds.toSet() }
        val known = _state.value.speakers.associateBy { it.id }

        /*
         * Everything heard from, not a filtered subset.
         *
         * What each device *is* decides which list it lands in later; a
         * device is never dropped here, because a Hub that is present and
         * unmentioned looks exactly like discovery having failed.
         */
        val live = table.online(now).map { peer ->
            val existing = known[peer.id]
            Speaker(
                id = peer.id,
                name = peer.name,
                address = peer.address,
                controlPort = peer.controlPort,
                canReceiveAudio = peer.announce.canReceiveAudio,
                canBeToldToPlay = peer.announce.canBeToldToPlay,
                fw = peer.announce.fw,
                selected = peer.id in chosen,
                volume = existing?.volume ?: -1,
                roomCorrection = existing?.roomCorrection ?: false,
                answered = existing?.answered ?: false,
                online = true,
            )
        }

        /*
         * And the ones that were chosen and have gone quiet, at their last
         * known address. Only the chosen ones: an unticked device that
         * left is simply gone, and a list that never forgets anything
         * becomes a list of everything that has ever been switched on.
         */
        val liveIds = live.map { it.id }.toSet()
        val missing = known.values.filter { it.id in chosen && it.id !in liveIds }
            .map { it.copy(selected = true, online = false) }

        _state.update { it.copy(speakers = live + missing) }
        sender?.setDestinations(currentDestinations())

        // What each one currently thinks its volume and correction are.
        // Only the ones this app can actually ask: the Hub serves a
        // different API on a different port and would 404 all day.
        scope.launch { pollNodes(live + missing) }
    }

    /**
     * Asks the chosen speakers how they are, over TCP.
     *
     * Two jobs in one request. It fills in volume and room correction for
     * a speaker that has not answered yet — which is what it was written
     * for — and it doubles as **a second liveness channel**, which is what
     * it is really worth. A unicast HTTP request that succeeds is much
     * better evidence that a speaker is present than a multicast announce
     * that happens to arrive, and it works in the case that produced this
     * whole change: a device sitting there perfectly healthy whose
     * announces are being eaten by the air.
     *
     * A device that answers is marked heard, so it never expires while it
     * is still talking to us.
     */
    private suspend fun pollNodes(speakers: List<Speaker>) {
        for (speaker in speakers) {
            if (!speaker.canReceiveAudio) continue
            // Ask a speaker we have never heard from, and keep asking the
            // ticked ones: those are the ones whose absence costs music.
            if (speaker.answered && !speaker.selected) continue

            val status = NodeClient(speaker.address, speaker.controlPort).status()
            if (status == null) continue

            discovery?.peers?.answered(speaker.id, System.currentTimeMillis())
            _state.update { current ->
                current.copy(speakers = current.speakers.map {
                    if (it.id == speaker.id) {
                        it.copy(
                            volume = status.volume,
                            roomCorrection = status.eqEnabled,
                            answered = true,
                            online = true,
                        )
                    } else {
                        it
                    }
                })
            }
        }
    }

    /**
     * Asks again, early, when a chosen speaker has gone quiet.
     *
     * The protocol has a probe precisely for this: a controller asks and
     * every device replies at once, instead of waiting up to five seconds
     * for the next scheduled announce. Sending one as soon as a ticked
     * speaker is halfway to expiring turns a run of lost datagrams into a
     * question rather than a disappearance.
     *
     * Not sent more than every [PROBE_INTERVAL_MS], and not sent at all
     * while everything chosen is present — a probe makes every device on
     * the network answer, and doing that twice a second would be its own
     * kind of rudeness.
     */
    private var lastProbeMs = 0L

    private fun probeIfQuiet() {
        val client = discovery ?: return
        val now = System.currentTimeMillis()
        if (now - lastProbeMs < PROBE_INTERVAL_MS) return

        val chosen = synchronized(selectedIds) { selectedIds.toSet() }
        if (chosen.isEmpty()) return

        val quiet = chosen.any { id ->
            val silent = client.peers.silentFor(id, now)
            silent == null || silent > QUIET_MS
        }
        if (!quiet) return

        lastProbeMs = now
        try {
            client.probe()
        } catch (e: Exception) {
            Log.w(TAG, "probe failed", e)
        }
    }

    /**
     * The cast point's supervisor, and the only thing awake when nothing
     * is playing.
     *
     * [pollCounters] runs `while (streaming)`, so it stops the moment the
     * music does — which is exactly when a published-but-idle cast point
     * needs watching. This loop runs for the life of the app and does
     * three things:
     *
     *  - **Republishes a dead one.** librespot exiting used to be the end
     *    of it. The overnight run is the evidence: the queue emptied,
     *    librespot went inactive, and the panel sat there for hours with
     *    nothing published and no way back short of pressing a button.
     *    Backed off, so a binary that cannot exec at all does not become a
     *    restart storm.
     *  - **Hands the stream back on an incoming cast.** Somebody picking
     *    the device in Spotify is a request, and it arrives on librespot's
     *    stderr rather than through this app — so this is where it is
     *    noticed. Held off briefly after a choice at the panel, or the two
     *    ends fight over the speakers.
     *  - **Falls back to available when a cast ends.** Otherwise the
     *    stream stays open forever and "published, waiting" becomes the
     *    permanent state of the screen, which is what made an idle panel
     *    look busy.
     */
    private fun watchCastPoint() {
        scope.launch {
            var everCast = false
            var retryMs = CAST_RETRY_MIN_MS
            var nextTryAt = 0L

            while (isActive) {
                val point = castPoint
                val context = appContext
                val now = System.currentTimeMillis()

                if (point != null && context != null) {
                    if (!point.isPlaying) {
                        /*
                         * Gone. Give the stream up if it was the source,
                         * then put it back on the network.
                         */
                        if (source === point) stopStream()
                        if (now >= nextTryAt) {
                            Log.i(TAG, "cast point is down; republishing")
                            point.shutDown()
                            castPoint = null
                            everCast = false
                            refreshCastPoint(context)
                            nextTryAt = now + retryMs
                            retryMs = (retryMs * 2).coerceAtMost(CAST_RETRY_MAX_MS)
                        }
                    } else {
                        retryMs = CAST_RETRY_MIN_MS

                        if (point.casting) {
                            everCast = true
                            if (source !== point && now >= ignoreCastsUntil) {
                                Log.i(TAG, "a cast arrived; Spotify takes the stream")
                                startStream(point)
                            }
                        } else if (source === point && everCast) {
                            /*
                             * The queue ran out, or the phone that was
                             * casting went away. Not a fault: back to
                             * published and waiting, which is where this
                             * device rests.
                             */
                            everCast = false
                            stopStream()
                        }
                    }

                    _state.update {
                        it.copy(
                            castPointUp = castPoint?.isPlaying == true,
                            castPointCasting = castPoint?.casting == true,
                        )
                    }
                }

                delay(1_000)
            }
        }
    }

    private fun pollCounters() {
        scope.launch {
            while (_state.value.streaming) {
                val rtp = sender
                if (rtp != null) {
                    _state.update {
                        it.copy(
                            packetsSent = rtp.packetsSent,
                            packetsHeld = rtp.packetsHeld,
                            sendingAudio = rtp.sendingAudio,
                            sendGaps = rtp.sendGaps.gaps,
                            sendGapWorstMs = rtp.sendGaps.worstMs,
                            sendGapShape = rtp.sendGaps.buckets.toList(),
                            underruns = rtp.underruns,
                            resyncs = rtp.resyncs,
                        )
                    }
                }

                /*
                 * And what the source says about itself.
                 *
                 * Read every second because librespot's most useful line —
                 * whether it authenticated — arrives seconds after it
                 * starts, and a person watching the screen should not have
                 * to guess whether it ever will.
                 */
                val playing = source
                val said = playing?.log ?: emptyList()
                val ready = playing?.ready
                val what = playing?.nowPlaying
                if (said != _state.value.sourceLog || ready != _state.value.sourceReady
                    || what != _state.value.nowPlaying
                ) {
                    _state.update {
                        it.copy(sourceLog = said, sourceReady = ready, nowPlaying = what)
                    }
                }

                /*
                 * A source that stopped on its own.
                 *
                 * librespot failing to start is the case that matters: the
                 * process cannot exec, the thread exits, and nothing else
                 * would ever notice — the screen would sit showing "Stop"
                 * for a stream that ended before it began.
                 */
                if (playing != null && !playing.isPlaying) {
                    // Its own last words, not a pointer at a log on a
                    // computer the person holding the phone may not have.
                    // A station that will not open lands here, which is
                    // where a bad URL should arrive.
                    val why = said.takeLast(3).joinToString("\n")
                    warn("${playing.label} stopped on its own." +
                        if (why.isNotEmpty()) "\n\nIt last said:\n$why" else "")
                    stopStream()
                }
                kotlinx.coroutines.delay(1_000)
            }
        }
    }

    /* ---------- the Spotify account ---------- */

    /**
     * Signs the cast point in, once.
     *
     * Not a login screen: no password is typed here and none is stored.
     * librespot prints an authorisation URL, the phone's browser opens it,
     * Spotify redirects to a listener on this same handset, and a
     * credential lands in app-private storage. After that the cast point
     * is claimed and simply appears in the picker.
     */
    fun signInToSpotify(context: Context, name: String, openUrl: (String) -> Unit) {
        if (_state.value.signingIn) return

        /*
         * A publishing stream is the other librespot, and it must go first.
         *
         * Both instances carry the cast point's name, and `LIBRESPOT.md`
         * records what two of those do to each other: Spotify offers
         * whichever it heard last, and playing to the wrong one fails in a
         * way that reads as a broken speaker. Signing in while publishing
         * would set that up deliberately.
         */
        if (_state.value.streaming) {
            stopStream()
        }

        _state.update { it.copy(signingIn = true) }
        scope.launch {
            val ok = try {
                SpotifyAccount.signIn(context, name, onUrl = openUrl)
            } catch (e: Exception) {
                Log.e(TAG, "sign-in failed", e)
                false
            }
            _state.update {
                it.copy(signingIn = false, spotifySignedIn = SpotifyAccount.isSignedIn(context))
            }
            // Publish straight away, so the device is in Spotify's list by
            // the time somebody looks for it rather than after one more press.
            /*
             * Success says nothing, and that is the point.
             *
             * A card announcing the sign-in worked was made redundant the
             * moment the cast point became a standing indicator: the lamp
             * appears, carrying the name, and goes on saying so for as
             * long as it is true. Following that with a sentence saying
             * the same thing — and a Dismiss button, so the person has to
             * put away a message about something that is still the case —
             * is noise stacked on top of the signal.
             *
             * The best confirmation an action can have is the thing it was
             * supposed to do, visibly having happened.
             *
             * Failure is the opposite and keeps its card. Nothing else on
             * the screen can explain why librespot did not authenticate,
             * and its own last words are the only evidence there is —
             * sending somebody to `adb logcat` is sending them to find a
             * computer.
             */
            if (ok) {
                refreshCastPoint(context)
            } else {
                val said = SpotifyAccount.lastOutput()
                    .filter { it.isNotBlank() }
                    .takeLast(4)
                    .joinToString("\n")
                warn(
                    "Sign-in did not finish." +
                        if (said.isNotEmpty()) "\n\nlibrespot said:\n$said" else ""
                )
            }
        }
    }

    /** Stops a sign-in that is waiting for the browser. */
    fun cancelSpotifySignIn() {
        SpotifyAccount.cancelSignIn()
    }

    /**
     * Forgets the account and clears anything left behind.
     *
     * Also stops a sign-in still in flight, because "start over" that
     * leaves the last attempt running is not starting over — the old
     * process keeps the OAuth port and the next try fails on a conflict
     * nobody would connect to this button.
     */
    fun forgetSpotify(context: Context) {
        SpotifyAccount.stopAnySignIn()
        SpotifyAccount.forget(context)
        if (source === castPoint) stopStream()
        castPoint?.shutDown()
        castPoint = null
        _state.update {
            it.copy(
                spotifySignedIn = false,
                signingIn = false,
                castPointUp = false,
                castPointCasting = false,
            )
        }
        warn("Spotify account forgotten, and anything still running stopped. " +
            "Sign in again for a fresh attempt.")
    }

    /** Reads the signed-in state from disk, for the first draw. */
    fun readSpotifyState(context: Context) {
        _state.update { it.copy(spotifySignedIn = SpotifyAccount.isSignedIn(context)) }
    }

    /* ---------- the two settings ---------- */

    fun readSettings(context: Context) {
        _state.update {
            it.copy(
                castName = Prefs.castName(context),
                showDetails = Prefs.showDetails(context),
                wallPanel = Prefs.wallPanel(context),
                stations = Prefs.stations(context),
                tracks = Prefs.tracks(context),
                castPointOn = Prefs.castPoint(context),
            )
        }
    }

    /**
     * Renames the cast point.
     *
     * Takes effect the next time something is published. A running
     * librespot advertises the name it was started with and there is no
     * way to tell it otherwise, so renaming mid-stream would leave the
     * screen and the Spotify picker disagreeing — which is worse than
     * waiting until the next publish, and is what the screen says.
     */
    fun setCastName(context: Context, name: String) {
        Prefs.setCastName(context, name)
        val renamed = Prefs.castName(context)
        _state.update { it.copy(castName = renamed) }

        /*
         * The announce, which used to be the one place a rename did not
         * reach.
         *
         * `ProducerService` builds an `Announce` once and hands it to
         * `attach`, so the name this phone puts on the network was the one
         * it had when the service started. Renaming updated the
         * preference, the screen and librespot, and left every other
         * device on the network — the Hub included — showing the old name
         * until the app was restarted. From outside, a rename that did not
         * take.
         *
         * The id is deliberately untouched. Every peer table out there
         * keys this device on it, and changing it would make the rename
         * look like a new device arriving beside a ghost of the old one.
         */
        discovery?.let { client ->
            client.self = client.self?.copy(name = renamed)
        }

        // The name is a process argument to librespot, so a rename is a
        // new cast point rather than a running one told otherwise.
        refreshCastPoint(context)
    }

    fun setShowDetails(context: Context, show: Boolean) {
        Prefs.setShowDetails(context, show)
        _state.update { it.copy(showDetails = show) }
    }

    /**
     * Wall-panel mode, which the activity applies on its next resume.
     *
     * Stored and mirrored into state in one step, like the details switch:
     * the screen reads state, the activity reads Prefs, and both have to
     * see the change for the switch to move *and* the window to follow.
     */
    fun setWallPanel(context: Context, on: Boolean) {
        Prefs.setWallPanel(context, on)
        _state.update { it.copy(wallPanel = on) }
    }

    /* ---------- the cast point ---------- */

    /**
     * Brings librespot up or down to match the switch and the account.
     *
     * Called whenever any of the three things it depends on can have
     * changed — the switch, the sign-in, the name — and safe to call when
     * nothing has. It is the only place that decides whether the cast
     * point should exist, so there is one answer rather than one per
     * caller.
     *
     * A cast point that is currently *the source* is left alone even if
     * the switch went off: pulling the stream out from under music that
     * is playing is not what "stop offering this in future" means. It
     * goes down when the stream does.
     */
    fun refreshCastPoint(context: Context) {
        val app = context.applicationContext
        val wanted = SpotifyAccount.isSignedIn(app) && Prefs.castPoint(app)
        val up = castPoint

        if (!wanted) {
            if (up != null && up !== source) {
                up.shutDown()
                castPoint = null
            }
            _state.update { it.copy(castPointUp = false, castPointOn = Prefs.castPoint(app)) }
            return
        }

        /*
         * A rename means a new cast point, because the name is a process
         * argument and librespot advertises the one it was started with.
         * Rebuilt rather than renamed, which is also what the Settings
         * text has always promised.
         */
        val name = Prefs.castName(app)
        if (up != null && up.label != "Spotify — $name") {
            if (up === source) stopStream()
            up.shutDown()
            castPoint = null
        }

        val point = castPoint ?: SpotifySource(app, name).also { castPoint = it }
        point.publish()
        _state.update { it.copy(castPointUp = true, castPointOn = true) }
    }

    /** The sticky switch, and the cast point follows it immediately. */
    fun setCastPoint(context: Context, on: Boolean) {
        Prefs.setCastPoint(context, on)
        _state.update { it.copy(castPointOn = on) }
        refreshCastPoint(context)
    }

    /**
     * The Stop button, which had to learn about the cast point.
     *
     * `stopStream()` releases the stream, and for anything this device
     * produces that is the whole of stopping. For a cast it was not even
     * close: the watcher would see a cast still in progress a second
     * later, find that nothing held the stream, and hand it straight back.
     * Stop appeared to do nothing at all.
     *
     * So stopping a cast ejects it — librespot restarts and whoever was
     * casting is dropped cleanly — and the guard window keeps them from
     * transferring straight back onto a device that has just reappeared.
     * The cast point returns unclaimed a second later, still in the list,
     * which is what the switch is for if somebody wants it gone for good.
     *
     * Everything else stops the way it always did.
     */
    fun stopPlayback() {
        if (source === castPoint && castPoint != null) {
            ejectCast("stopped at the panel")
        }
        stopStream()
    }

    /**
     * Ejects whoever is casting, without taking the device out of the list.
     *
     * Called when the panel chooses something else. The alternative —
     * detaching and leaving librespot running — reads much worse from the
     * guest's side: their phone goes on showing the track playing, the
     * progress bar moving, while nothing comes out of anything. A device
     * that disappears for a second and returns is a thing people
     * understand; silent phantom playback is not.
     *
     * It comes straight back, unclaimed, so the cast point stays sticky
     * and anyone can take it again — which is the point.
     */
    private fun ejectCast(reason: String) {
        val point = castPoint ?: return
        val context = appContext ?: return
        if (!point.casting) return

        Log.i(TAG, "restarting the cast point: $reason")
        point.shutDown()
        castPoint = null
        ignoreCastsUntil = System.currentTimeMillis() + CAST_GRACE_MS
        refreshCastPoint(context)
    }

    /* ---------- radio stations ---------- */

    /**
     * Saves a station.
     *
     * The URL is kept exactly as pasted. A playlist URL is the durable
     * address of a station and the stream URLs behind it move, so it is
     * resolved afresh at each play rather than once here — the Hub's rule,
     * and the reason a station saved a year ago still works.
     */
    fun addStation(context: Context, name: String, url: String) {
        val cleanName = name.trim().ifEmpty { url.trim().substringAfter("://").substringBefore('/') }
        val cleanUrl = url.trim()
        if (cleanUrl.isEmpty()) return

        val existing = _state.value.stations
        val station = Station(
            id = Station.idFor(cleanName, existing.map { it.id }.toSet()),
            name = cleanName,
            url = cleanUrl,
        )
        val updated = existing + station
        Prefs.setStations(context, updated)
        _state.update { it.copy(stations = updated) }
    }

    fun removeStation(context: Context, id: String) {
        val updated = _state.value.stations.filterNot { it.id == id }
        Prefs.setStations(context, updated)
        _state.update { it.copy(stations = updated) }
    }

    /* ---------- files from this phone ---------- */

    /**
     * Remembers a file the picker just handed over, and plays it.
     *
     * The caller has already taken the persistable read grant — it has to,
     * because the grant is only offered to whoever received the result —
     * and this is what makes that grant worth taking. Before this existed
     * the app took one on every pick and then dropped the URI on the floor,
     * so the permission outlived the only thing that could have used it.
     */
    fun addTrack(context: Context, name: String, uri: Uri) {
        val existing = _state.value.tracks
        val updated = Track.add(existing, name, uri.toString())
        if (updated === existing) return
        saveTracks(context, existing, updated)
        playTrack(context, updated.first())
    }

    /**
     * Plays one that is already in the list.
     *
     * A dead URI — a deleted file, an unmounted card, a grant that did not
     * survive — arrives here like any other and fails inside the decoder,
     * where `LibrarySource` logs it and simply never starts playing. That
     * shows up as a stream that publishes and sends nothing, which is the
     * same picture as a station that will not open, and Remove is the way
     * out of both.
     */
    fun playTrack(context: Context, track: Track) {
        startStream(LibrarySource(context.applicationContext, Uri.parse(track.uri), track.name))
    }

    fun removeTrack(context: Context, id: String) {
        val existing = _state.value.tracks
        saveTracks(context, existing, existing.filterNot { it.id == id })
    }

    /**
     * Writes the list, and gives back the grants nothing points at any more.
     *
     * One path for both ways an entry leaves — Remove, and falling off the
     * end of [Track.LIMIT] — because the consequence is the same either
     * way. A persistable grant that is not released is not harmless: a
     * package may hold only so many, and once the system's own cap is
     * reached it starts revoking the oldest, which would silently break
     * entries still on this list.
     */
    private fun saveTracks(context: Context, before: List<Track>, after: List<Track>) {
        Prefs.setTracks(context, after)
        _state.update { it.copy(tracks = after) }

        val kept = after.map { it.uri }.toSet()
        for (gone in before.filterNot { it.uri in kept }) {
            try {
                context.contentResolver.releasePersistableUriPermission(
                    Uri.parse(gone.uri), Intent.FLAG_GRANT_READ_URI_PERMISSION
                )
            } catch (_: SecurityException) {
                // Already gone — app data cleared, or the provider withdrew
                // it. Releasing what we do not hold is not a failure.
            }
        }
    }

    /**
     * The name this phone wears on the network, and in Spotify.
     *
     * One name for both, deliberately: a device that appears as one thing
     * in the Spotify picker and another in the speaker list is two devices
     * as far as anybody looking at it is concerned.
     */
    fun castPointName(context: Context): String = Prefs.castName(context)

    /** The announce this phone sends, so it appears like any other device. */
    fun identity(name: String, id: String): Announce = Announce(
        oal = Discovery.SUITE,
        id = id,
        name = name,
        roles = listOf("producer"),
        hw = "android",
        fw = BuildInfo.VERSION,
        caps = emptyList(),
        ctrlPort = null,
    )
}

/** How often a probe may be sent while something chosen is missing. */
private const val PROBE_INTERVAL_MS = 5_000L

/**
 * How long a choice made at the panel outranks an arriving cast.
 *
 * Long enough for a phone that was ejected to finish transferring itself
 * back to its own speaker and give up, short enough that somebody who
 * genuinely wants to cast a moment later is not refused.
 */
private const val CAST_GRACE_MS = 8_000L

/** How soon a cast point that went down is put back, and the ceiling. */
private const val CAST_RETRY_MIN_MS = 3_000L
private const val CAST_RETRY_MAX_MS = 60_000L

/**
 * How long a chosen speaker may be silent before it is asked directly.
 *
 * Well inside the 30-second liveness window, so the question is asked
 * while there is still time for the answer to keep the device on the list.
 */
private const val QUIET_MS = 10_000L

/**
 * The app's version, from the one place that defines it.
 *
 * This used to be a hand-written constant beside the `versionName` in
 * `app/build.gradle.kts`, and keeping two numbers in step by hand worked
 * exactly as well as that always works: they drifted whenever one was
 * forgotten, and the announce this phone sends then claimed a version
 * the APK was not.
 *
 * `BuildConfig.VERSION_NAME` is generated from `versionName`, so there is
 * now one number. That also matters for what comes next — a release
 * derives its version from the git tag, and a second copy in Kotlin
 * would be a second thing for the tag to disagree with.
 */
object BuildInfo {
    val VERSION: String = BuildConfig.VERSION_NAME
}
