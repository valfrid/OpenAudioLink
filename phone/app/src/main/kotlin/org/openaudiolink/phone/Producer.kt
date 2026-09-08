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

    private val ring = PcmRing(capacityFrames = Rtp.SAMPLE_RATE)   // one second
    private var sender: RtpSender? = null
    private var discovery: DiscoveryClient? = null
    private var binding: WifiBinding? = null
    private var source: AudioSource? = null

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
        val wifi = WifiBinding(context).also { binding = it }
        wifi.acquireLocks()
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
                _state.update { it.copy(discovering = true, listeningOn = client.joinedOn) }
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

        val rtp = RtpSender(ring, socketProvider = {
            /*
             * Bound to the Wi-Fi where there is one, unbound where there is
             * not — never refused.
             *
             * A phone *hosting* the hotspot has no station network at all,
             * so `wifiNetwork()` is null and there is nothing to bind to;
             * that is the party-mode arrangement of decision 20, not a
             * fault. Refusing to open a socket there would have made the
             * one deployment this app exists for the one it cannot do.
             */
            wifi.boundSocket() ?: java.net.DatagramSocket()
        })
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
        _state.update { current ->
            current.copy(speakers = current.speakers.map {
                if (it.id == id) it.copy(selected = !it.selected) else it
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
     * A speaker that dropped off the network and came back must not lose
     * its tick — at a party that is the difference between "it reappeared"
     * and "it reappeared and went silent".
     */
    private fun refreshSpeakers() {
        val table = discovery?.peers ?: return
        val now = System.currentTimeMillis()
        val chosen = _state.value.speakers.filter { it.selected }.map { it.id }.toSet()
        val known = _state.value.speakers.associateBy { it.id }

        /*
         * Everything heard from, not a filtered subset.
         *
         * What each device *is* decides which list it lands in later; a
         * device is never dropped here, because a Hub that is present and
         * unmentioned looks exactly like discovery having failed.
         */
        val speakers = table.online(now).map { peer ->
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
            )
        }

        _state.update { it.copy(speakers = speakers) }
        sender?.setDestinations(currentDestinations())

        // What each one currently thinks its volume and correction are.
        // Only the ones this app can actually ask: the Hub serves a
        // different API on a different port and would 404 all day.
        scope.launch {
            for (speaker in speakers.filter { !it.answered && it.canReceiveAudio }) {
                val status = NodeClient(speaker.address, speaker.controlPort).status() ?: continue
                _state.update { current ->
                    current.copy(speakers = current.speakers.map {
                        if (it.id == speaker.id) {
                            it.copy(
                                volume = status.volume,
                                roomCorrection = status.eqEnabled,
                                answered = true,
                            )
                        } else {
                            it
                        }
                    })
                }
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

    /**
     * The name this phone wears on the network, and in Spotify.
     *
     * The phone's own name, because at a party that is the one a guest
     * recognises: "Anna's phone" tells four people in a room which device
     * is which, where a product name would show all four the same thing.
     */
    fun castPointName(context: Context): String =
        android.provider.Settings.Global.getString(
            context.contentResolver, android.provider.Settings.Global.DEVICE_NAME
        ) ?: android.os.Build.MODEL ?: "OpenAudioLink"

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

object BuildInfo {
    const val VERSION = "0.6.5"
}
