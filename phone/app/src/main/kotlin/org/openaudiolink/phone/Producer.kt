package org.openaudiolink.phone

import android.content.Context
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
import org.openaudiolink.core.PcmRing
import org.openaudiolink.core.Rtp
import org.openaudiolink.core.RtpSender
import org.openaudiolink.core.Station
import org.openaudiolink.phone.sources.AudioSource
import org.openaudiolink.phone.sources.SpotifyAccount
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
        /** Whether the cast point has ever been signed in. */
        val spotifySignedIn: Boolean = false,
        /** Set while the one-time sign-in is waiting for the browser. */
        val signingIn: Boolean = false,
        /** Something a person needs to be told, in their own words. */
        val warning: String? = null,
        /** What this phone calls itself, on the network and in Spotify. */
        val castName: String = "",
        /** Saved radio stations, in the order they were added. */
        val stations: List<Station> = emptyList(),
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
                if (said != _state.value.sourceLog || ready != _state.value.sourceReady) {
                    _state.update { it.copy(sourceLog = said, sourceReady = ready) }
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
            warn(
                if (ok) {
                    /*
                     * Not "it will now appear". Nothing appears until
                     * something is publishing: the sign-in run is killed
                     * once it has the credential, so at this exact moment
                     * there is no receiver on the network at all.
                     */
                    "Signed in. Now press \"Publish to Spotify\" and leave it " +
                        "running — \"$name\" only shows in Spotify's device list " +
                        "while it does."
                } else {
                    /*
                     * librespot's own words, on the phone.
                     *
                     * Sending somebody to `adb logcat` is sending them to
                     * find a computer, and the lines that explain a failed
                     * sign-in belong on the handset that failed.
                     */
                    val said = SpotifyAccount.lastOutput()
                        .filter { it.isNotBlank() }
                        .takeLast(4)
                        .joinToString("\n")
                    "Sign-in did not finish." +
                        if (said.isNotEmpty()) "\n\nlibrespot said:\n$said" else ""
                }
            )
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
        _state.update { it.copy(spotifySignedIn = false, signingIn = false) }
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
                stations = Prefs.stations(context),
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
        _state.update { it.copy(castName = Prefs.castName(context)) }
    }

    fun setShowDetails(context: Context, show: Boolean) {
        Prefs.setShowDetails(context, show)
        _state.update { it.copy(showDetails = show) }
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
 * How long a chosen speaker may be silent before it is asked directly.
 *
 * Well inside the 30-second liveness window, so the question is asked
 * while there is still time for the answer to keep the device on the list.
 */
private const val QUIET_MS = 10_000L

object BuildInfo {
    const val VERSION = "0.8.8"
}
