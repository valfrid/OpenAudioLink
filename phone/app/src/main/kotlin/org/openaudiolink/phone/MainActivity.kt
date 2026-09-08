package org.openaudiolink.phone

import android.Manifest
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Checkbox
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import org.openaudiolink.core.Rtp
import org.openaudiolink.phone.sources.LibrarySource
import org.openaudiolink.phone.sources.SpotifySource
import org.openaudiolink.phone.sources.ToneSource
import kotlin.math.roundToInt

/**
 * The whole control surface, on one screen.
 *
 * Decision 19: the phone carries only enough Controller to get a stream
 * running — choose speakers, start and stop, volume, room correction. What
 * is deliberately not here is the Hub's half: no OTA, no sample log, no
 * room measurement. A speaker holds its own correction in NVS, so it
 * travels to the party without the phone knowing the measurement was ever
 * made.
 */
class MainActivity : ComponentActivity() {

    private val pickTrack = registerForActivityResult(
        ActivityResultContracts.OpenDocument()
    ) { uri: Uri? ->
        if (uri != null) {
            contentResolver.takePersistableUriPermission(
                uri, Intent.FLAG_GRANT_READ_URI_PERMISSION
            )
            Producer.startStream(LibrarySource(applicationContext, uri, nameOf(uri)))
        }
    }

    private val askNotifications = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { /* Declined only costs the notification, not the audio. */ }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            askNotifications.launch(Manifest.permission.POST_NOTIFICATIONS)
        }

        /*
         * The screen first, the service second.
         *
         * A service that dies in onCreate takes the process with it, and
         * starting it before setContent meant the app crashed before
         * drawing a single frame — so the one thing that could have said
         * what was wrong was the thing that never appeared. Drawing first
         * costs nothing and means a failure is something a person can
         * read.
         */
        setContent {
            MaterialTheme {
                Scaffold { padding ->
                    Screen(
                        modifier = Modifier.padding(padding),
                        onPickTrack = { pickTrack.launch(arrayOf("audio/*")) },
                    )
                }
            }
        }

        ProducerService.start(this)
    }

    private fun nameOf(uri: Uri): String =
        uri.lastPathSegment?.substringAfterLast('/') ?: "Track"
}

@Composable
private fun Screen(modifier: Modifier = Modifier, onPickTrack: () -> Unit) {
    val state by Producer.state.collectAsState()
    val context = LocalContext.current

    /*
     * What the cast point is called in everyone's Spotify picker.
     *
     * The phone's own name, because at a party that is the name a guest
     * will recognise — "Anna's phone" rather than a product name four
     * people in the room would each see identically.
     */
    val castPointName = remember { Producer.castPointName(context) }

    Column(modifier.fillMaxSize().padding(16.dp)) {

        state.warning?.let { warning ->
            Card(Modifier.fillMaxWidth().padding(bottom = 12.dp)) {
                Column(Modifier.padding(12.dp)) {
                    Text(warning, style = MaterialTheme.typography.bodyMedium)
                    TextButton(onClick = { Producer.dismissWarning() }) { Text("Dismiss") }
                }
            }
        }

        /*
         * The order on screen is the order of the job: pick the speakers,
         * then pick what plays through them. The first build had it the
         * other way round, with a "Choose a track" button above a list
         * titled "Speakers" that also held a Windows PC — so nothing said
         * what the button would do or which of those things it would do it
         * to.
         */
        val chosen = state.selected.size

        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
            Text("Play on", style = MaterialTheme.typography.titleLarge)
            TextButton(onClick = { Producer.probe() }) { Text("Look again") }
        }
        Text(
            when {
                state.destinations.isNotEmpty() ->
                    "Tick the speakers that should play. $chosen chosen."
                /*
                 * "Still looking" and "never started" are different, and
                 * only one of them is worth waiting through. Saying which
                 * also distinguishes a working app from a dead one: with
                 * nothing found, every control below is legitimately
                 * disabled, and a screen full of grey buttons is exactly
                 * what a frozen app looks like.
                 */
                !state.discovering ->
                    "Not looking for speakers — discovery did not start. " +
                        "Reopening the app is the quickest thing to try."
                state.speakers.isNotEmpty() ->
                    "Listening… ${state.speakers.size} device(s) heard, none of " +
                        "them a speaker yet."
                else ->
                    "Listening… none heard yet. Speakers must be on this Wi-Fi — " +
                        "and if this phone is the hotspot it has to be 2.4 GHz, " +
                        "because a speaker's radio cannot see 5 GHz at all."
            },
            style = MaterialTheme.typography.bodyMedium,
        )

        /*
         * The heartbeat, and the reason it is on screen rather than in a
         * log.
         *
         * With no speakers found every control is disabled, and a screen of
         * grey buttons is what a hung app looks like — there was no way to
         * tell a working app from a dead one by looking. A count that moves
         * settles it in two seconds, and the interface name answers the
         * next question after that: heard 0 on wlan0 is a quiet network,
         * heard 0 on the wrong interface is a bug.
         */
        if (state.discovering) {
            Text(
                "on ${state.listeningOn ?: "?"} · heard ${state.datagramsHeard} · " +
                    "announced ${state.announcesSent} · probes ${state.probesSent}" +
                    (state.discoveryError?.let { " · $it" } ?: ""),
                style = MaterialTheme.typography.bodySmall,
            )
        }

        LazyColumn(
            Modifier.weight(1f, fill = false),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            items(state.destinations, key = { it.id }) { speaker ->
                SpeakerCard(speaker)
            }

            if (state.sources.isNotEmpty()) {
                item {
                    Spacer(Modifier.height(16.dp))
                    Text("Or play something already on the network",
                        style = MaterialTheme.typography.titleLarge)
                    Text(
                        "These make their own sound — a turntable, say. This phone " +
                            "only tells them where to send it.",
                        style = MaterialTheme.typography.bodyMedium,
                    )
                }
                items(state.sources, key = { it.id }) { source ->
                    SourceCard(source, enabled = chosen > 0)
                }
            }

            if (state.others.isNotEmpty()) {
                item {
                    Spacer(Modifier.height(16.dp))
                    Text("Also on the network", style = MaterialTheme.typography.titleLarge)
                    Text(
                        "Found, but not driven from here. The Hub has its own " +
                            "controls — this phone does not command it.",
                        style = MaterialTheme.typography.bodyMedium,
                    )
                }
                items(state.others, key = { it.id }) { other ->
                    Card(Modifier.fillMaxWidth()) {
                        Column(Modifier.padding(12.dp)) {
                            Text(other.name, style = MaterialTheme.typography.titleMedium)
                            Text("${other.address} · not a speaker",
                                style = MaterialTheme.typography.bodySmall)
                        }
                    }
                }
            }
        }

        Spacer(Modifier.height(20.dp))
        Text("Play from this phone", style = MaterialTheme.typography.titleLarge)

        if (state.streaming) {
            /*
             * Two states, not one: **published** and **playing**.
             *
             * They used to be the same thing, because the sender pumped
             * padded silence from the moment it started — so pressing
             * Publish began counting packets before Spotify had been
             * opened, and the counter, the one signal that anything was
             * working, said "playing" about a phone that had nothing to
             * play. SilenceGate keeps the wire empty until there is audio;
             * this says which of the two is happening.
             */
            val waiting = !state.sendingAudio
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                Button(onClick = { Producer.stopStream() }) { Text("Stop") }
                Text(
                    (if (waiting) "Published · " else "Playing · ") + state.sourceLabel,
                    style = MaterialTheme.typography.titleMedium,
                )
            }

            if (waiting) {
                Text(
                    "Nothing is playing yet, so nothing is being sent — the " +
                        "speakers stay quiet until something starts.",
                    style = MaterialTheme.typography.bodyMedium,
                )
                if (state.sourceLabel?.startsWith("Spotify") == true) {
                    /*
                     * The three steps, in order, because this is the part
                     * that happens in another app and comes back.
                     */
                    Text(
                        "1. \"$castPointName\" is now in Spotify's device list.\n" +
                            "2. Open Spotify, pick it, and press play.\n" +
                            "3. Come back here — the packet count starts moving.",
                        style = MaterialTheme.typography.bodyMedium,
                    )
                }
                /*
                 * How long, not how many.
                 *
                 * The first version printed the held-packet count, and
                 * "155895 packets held" reads like something enormous is
                 * going on. It is a five-millisecond tick — thirteen
                 * minutes of nothing — so it is shown as the thirteen
                 * minutes it is.
                 */
                Text(
                    "waiting ${elapsed(state.packetsHeld)} · nothing sent" +
                        if (chosen == 0) " · nothing ticked" else " · $chosen speaker(s) ready",
                    style = MaterialTheme.typography.bodySmall,
                )
            } else {
                Text(
                    "${state.packetsSent} packets · ${state.underruns} underruns · " +
                        "${state.resyncs} resyncs" +
                        if (chosen == 0) " · to nobody" else " · to $chosen speaker(s)",
                    style = MaterialTheme.typography.bodySmall,
                )
            }

            /*
             * The source's own words, not this app's summary of them.
             *
             * The counters above say what this app is doing. They say
             * nothing about whether the source is well, and for Spotify
             * that is the whole question: librespot can be running and
             * publishing nothing, which from outside looks exactly like a
             * cast point Spotify has not listed yet.
             *
             * Authentication is called out rather than left in the
             * scroll, because it is the fork everything else hangs off.
             * Below it the last few lines verbatim — one line was not
             * enough: the one that arrived was librespot's stop handler
             * complaining it had no context to fall back to, which is
             * what happens *after* something goes wrong and not what.
             */
            state.sourceReady?.let { ready ->
                Spacer(Modifier.height(8.dp))
                Text(
                    if (ready) {
                        "librespot: signed in to Spotify."
                    } else {
                        "librespot has not signed in to Spotify yet. Until it " +
                            "does, picking this in the device list cannot play " +
                            "anything. Connect playback also needs a Premium " +
                            "account — librespot cannot stream on a free one."
                    },
                    style = MaterialTheme.typography.bodyMedium,
                )
            }

            if (state.sourceLog.isNotEmpty()) {
                Text(
                    state.sourceLog.takeLast(LOG_LINES_ON_SCREEN).joinToString("\n"),
                    style = MaterialTheme.typography.bodySmall,
                )
            }
        } else {
            Text(
                if (chosen > 0) {
                    "Sends this phone's audio to the $chosen ticked speaker(s)."
                } else {
                    /*
                     * Not disabled, and this says why it is still worth
                     * pressing: publishing the cast point before any
                     * speaker exists is how a party starts, and speakers
                     * can be added to a running stream without the ones
                     * already playing noticing.
                     */
                    "Nothing ticked — these will run and be heard by nobody " +
                        "until a speaker appears."
                },
                style = MaterialTheme.typography.bodyMedium,
            )
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(onClick = onPickTrack) { Text("A music file…") }
                // Needs no permission, no file and no account, so it
                // separates "is the network right" from "is the decoder
                // right" when a speaker is silent.
                OutlinedButton(onClick = { Producer.startStream(ToneSource()) }) {
                    Text("Test tone")
                }
            }

            /*
             * The reason the phone hub exists, so it is not tucked away
             * under the file picker.
             */
            Spacer(Modifier.height(16.dp))
            Text("Spotify", style = MaterialTheme.typography.titleLarge)

            /*
             * Signing in comes first, and the order is not a preference.
             *
             * Spotify clients do not show *unclaimed* zeroconf devices —
             * `docs/LIBRESPOT.md`, established over two evenings and proven
             * over loopback. So publishing before signing in produces a
             * cast point that announces perfectly and appears nowhere,
             * which is the most confusing possible outcome. The button that
             * fixes that is the one on top.
             */
            if (!state.spotifySignedIn) {
                Text(
                    "\"$castPointName\" has to sign in to Spotify once before it " +
                        "shows up in the device list — an unclaimed receiver is " +
                        "invisible there, however well it announces itself. No " +
                        "password is typed here: Spotify's own page opens, you " +
                        "approve, and it comes back.",
                    style = MaterialTheme.typography.bodyMedium,
                )
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(
                        onClick = {
                            Producer.signInToSpotify(context, castPointName) { url ->
                                context.startActivity(
                                    Intent(Intent.ACTION_VIEW, Uri.parse(url))
                                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                                )
                            }
                        },
                        enabled = !state.signingIn,
                    ) {
                        Text(if (state.signingIn) "Waiting for Spotify…" else "Sign in to Spotify")
                    }
                    /*
                     * There is no deadline on the wait any more, so there
                     * has to be a way to stop it. The first version gave it
                     * five minutes and then declared failure — while the
                     * person was still reading a code out of their email.
                     */
                    if (state.signingIn) {
                        OutlinedButton(onClick = { Producer.cancelSpotifySignIn() }) {
                            Text("Cancel")
                        }
                    } else {
                        /*
                         * Available before the first success, not only
                         * after it: a half-finished attempt can leave a
                         * process holding the OAuth port, and the way out
                         * of that should not be hidden behind having
                         * signed in.
                         */
                        OutlinedButton(onClick = { Producer.forgetSpotify(context) }) {
                            Text("Start over")
                        }
                    }
                }
                if (state.signingIn) {
                    Text(
                        "Take as long as you need — this waits. Come back to the app " +
                            "when Spotify says it is done.",
                        style = MaterialTheme.typography.bodySmall,
                    )
                }
            } else {
                /*
                 * "Signed in" is not "visible", and saying so cost an
                 * evening.
                 *
                 * Signing in claims the cast point against the account; it
                 * does not create one. The receiver only exists while
                 * librespot is running, and the sign-in run is deliberately
                 * killed once it has earned the credential — so between
                 * signing in and pressing Publish there is nothing on the
                 * network to find, and looking in Spotify then finds
                 * exactly nothing. The button is the thing that makes it
                 * appear, and the text now says so.
                 */
                Text(
                    "Signed in — but nothing is published yet. Press Publish and " +
                        "leave it running: that puts \"$castPointName\" in Spotify's " +
                        "device list and sends nothing yet. Then pick it in Spotify " +
                        "and press play, and the audio goes to the ticked speakers.",
                    style = MaterialTheme.typography.bodyMedium,
                )
                /*
                 * Said once, plainly, before the first attempt.
                 *
                 * librespot streams only for a Premium account. A free
                 * one signs in perfectly, claims the cast point, appears
                 * in the device list, and then plays nothing — which is
                 * indistinguishable from a bug in this app and would send
                 * anybody looking in entirely the wrong place.
                 */
                Text(
                    "Needs Spotify Premium: librespot cannot stream on a free " +
                        "account, though it will sign in and appear like one that can.",
                    style = MaterialTheme.typography.bodySmall,
                )
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(onClick = {
                        Producer.startStream(SpotifySource(context, castPointName))
                    }) {
                        Text("Publish to Spotify")
                    }
                    // The cast point belongs to the account that signed it
                    // in, so handing the phone on means forgetting it.
                    OutlinedButton(onClick = { Producer.forgetSpotify(context) }) {
                        Text("Forget account")
                    }
                }
            }
        }
    }
}

/** How many of librespot's lines fit on a phone without pushing the rest off. */
private const val LOG_LINES_ON_SCREEN = 6

/**
 * Held packets as the time they actually represent.
 *
 * A packet is 5 ms, so 200 of them is a second. Six figures of "packets
 * held" reads like a fault; "waiting 13m" reads like waiting, which is
 * what it is.
 */
private fun elapsed(packets: Long): String {
    val seconds = packets / (Rtp.SAMPLE_RATE / Rtp.FRAMES_PER_PACKET)
    return when {
        seconds < 60 -> "${seconds}s"
        seconds < 3600 -> "${seconds / 60}m ${seconds % 60}s"
        else -> "${seconds / 3600}h ${(seconds % 3600) / 60}m"
    }
}

@Composable
private fun SourceCard(source: Producer.Speaker, enabled: Boolean) {
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp)) {
            Text(source.name, style = MaterialTheme.typography.titleMedium)
            Text(source.address, style = MaterialTheme.typography.bodySmall)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(
                    onClick = { Producer.startVinyl(source.id) },
                    enabled = enabled,
                ) { Text("Play this") }
                OutlinedButton(onClick = { Producer.stopVinyl(source.id) }) { Text("Stop") }
            }
        }
    }
}

@Composable
private fun SpeakerCard(speaker: Producer.Speaker) {
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Checkbox(
                    checked = speaker.selected,
                    onCheckedChange = { Producer.toggleSpeaker(speaker.id) },
                )
                Column(Modifier.weight(1f)) {
                    Text(speaker.name, style = MaterialTheme.typography.titleMedium)
                    Text(speaker.address, style = MaterialTheme.typography.bodySmall)
                }
            }

            if (speaker.hasVolume) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("Volume", Modifier.weight(0.3f),
                        style = MaterialTheme.typography.bodyMedium)
                    Slider(
                        value = speaker.volume.toFloat(),
                        onValueChange = {
                            Producer.setVolume(speaker.id, it.roundToInt())
                        },
                        valueRange = 0f..100f,
                        modifier = Modifier.weight(1f),
                    )
                    Text("${speaker.volume}", style = MaterialTheme.typography.bodySmall)
                }
            } else if (speaker.volumeUnsupported) {
                // Firmware older than 0.11.0 has no volume at all, and a
                // slider at zero would be a lie about a speaker playing
                // perfectly well.
                Text("This speaker's firmware has no volume control",
                    style = MaterialTheme.typography.bodySmall)
            } else {
                /*
                 * Not the same sentence as the one above, and the
                 * difference cost a debugging session. "We have not heard
                 * back" is a question about this phone's reach; "no volume
                 * control" is a statement about the speaker. Saying the
                 * second when the first is true blames working firmware
                 * for a request that never left the handset.
                 */
                Text("No answer yet from this speaker",
                    style = MaterialTheme.typography.bodySmall)
            }

            if (!speaker.unreachable) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("Room correction", Modifier.weight(1f),
                        style = MaterialTheme.typography.bodyMedium)
                    Switch(
                        checked = speaker.roomCorrection,
                        onCheckedChange = { Producer.setRoomCorrection(speaker.id, it) },
                    )
                }
            }
        }
    }
}
