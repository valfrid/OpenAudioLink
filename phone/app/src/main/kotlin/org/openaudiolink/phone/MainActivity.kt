package org.openaudiolink.phone

import android.Manifest
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import org.openaudiolink.core.Rtp
import org.openaudiolink.core.Station
import org.openaudiolink.phone.sources.LibrarySource
import org.openaudiolink.phone.sources.RadioSource
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
 *
 * **Shaped like the Hub's `play.html`**, because it is the same job for
 * the same person: a brand line with a health dot, a banner for what is
 * playing, the rooms, and a row of tiles answering "what would you like to
 * hear". Two products that do the same thing should not need to be learned
 * twice.
 *
 * The counters, the heartbeat and librespot's log are all still here and
 * all now live behind *Show details*. They were written to answer real
 * questions during integration and they answered them; a screen that opens
 * on packet counts and another program's stderr is an instrument panel,
 * and this is meant to be a thing somebody plays music with.
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

        // The settings are read here as well as in the service, so the
        // first frame carries the real cast point name rather than a
        // placeholder that changes a moment later.
        Producer.readSettings(this)

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
            OalTheme {
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

    Column(
        modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Brand(state)

        state.warning?.let { warning ->
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(12.dp)) {
                    Text(warning, style = MaterialTheme.typography.bodyMedium)
                    TextButton(onClick = { Producer.dismissWarning() }) { Text("Dismiss") }
                }
            }
        }

        if (state.streaming) NowPlaying(state)

        Rooms(state)

        if (!state.streaming) {
            Sources(
                state = state,
                onPickTrack = onPickTrack,
                onSpotify = {
                    if (state.spotifySignedIn) {
                        Producer.startStream(SpotifySource(context, state.castName))
                    } else {
                        Producer.signInToSpotify(context, state.castName) { url ->
                            context.startActivity(
                                Intent(Intent.ACTION_VIEW, Uri.parse(url))
                                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                            )
                        }
                    }
                },
            )
        }

        Settings(state)

        if (state.showDetails) Details(state)
    }
}

/* ---------------------------------------------------------------- brand */

/**
 * The name, and one honest word about whether the app is alive.
 *
 * The dot is `play.html`'s, and it is here for the reason it was added
 * there: with no speakers found every control below is legitimately
 * disabled, and a screen of grey buttons is indistinguishable from a hung
 * app. One thing on screen that is unambiguously working settles it.
 */
@Composable
private fun Brand(state: Producer.State) {
    Row(
        Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column {
            /*
             * No weight override. `headlineSmall` is already Bold in the
             * theme, and Bold is one of the three weights bundled as a
             * real file — asking here for a fourth (SemiBold, as this
             * did) makes the platform synthesise the difference by
             * stroking the glyph, which is the artefact 0.8.7 exists to
             * remove.
             */
            Text(
                "OpenAudioLink",
                style = MaterialTheme.typography.headlineSmall,
            )
            /*
             * The version, on screen, always — not behind the details
             * switch and not only in the announce.
             *
             * This app is installed by hand from a CI artefact, so two
             * builds can differ by a feature and look identical, and a
             * screenshot is the only thing anybody has to go on. That
             * cost a round already: a report of a fault "in 0.8.4" was
             * really 0.8.3 still installed, and the only way to tell was
             * noticing that a field 0.8.4 adds was missing.
             *
             * The Hub's footer does exactly this — name, version,
             * protocol — for the same reason.
             */
            Text(
                "v${BuildInfo.VERSION}",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(
                Modifier
                    .size(8.dp)
                    .background(
                        if (state.discovering) MaterialTheme.colorScheme.primary
                        else MaterialTheme.colorScheme.error,
                        CircleShape,
                    )
            )
            Spacer(Modifier.size(6.dp))
            Text(
                when {
                    !state.discovering -> "Not listening"
                    state.destinations.isEmpty() -> "Listening"
                    else -> "${state.destinations.size} speaker(s)"
                },
                style = MaterialTheme.typography.bodySmall,
            )
        }
    }
}

/* -------------------------------------------------------- now playing */

/**
 * The banner, and the one control on it that must never be ambiguous.
 *
 * Published and Playing are different states and the wire shows the
 * difference — a cast point nobody has selected sends nothing. Saying
 * which is the difference between an app that looks broken and one that
 * is waiting.
 */
@Composable
private fun NowPlaying(state: Producer.State) {
    val chosen = state.selected.size
    Card(
        Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.secondaryContainer,
        ),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(
                if (state.sendingAudio) "Playing" else "Published, waiting",
                style = MaterialTheme.typography.labelLarge,
            )
            Text(
                state.sourceLabel ?: "",
                style = MaterialTheme.typography.titleMedium,
            )
            Text(
                when {
                    state.sendingAudio && chosen > 0 -> "Playing on $chosen speaker(s)."
                    state.sendingAudio -> "Nothing is ticked, so nobody can hear this."
                    state.sourceLabel?.startsWith("Spotify") == true ->
                        "Pick \"${state.castName}\" in Spotify and press play."
                    else -> "Nothing is playing yet, so nothing is being sent."
                },
                style = MaterialTheme.typography.bodyMedium,
            )
            /*
             * The one figure worth reading while it plays, at the top.
             *
             * It lives in Details as well, under Settings, which is below
             * two speaker cards and off the bottom of a phone screen —
             * far enough that two screenshots taken to answer this exact
             * question both stopped just short of it. A number nobody can
             * find has not been measured.
             */
            if (state.showDetails && state.sendingAudio) {
                Text(
                    "sent unevenly: ${state.sendGaps} gaps · worst " +
                        "${state.sendGapWorstMs} ms" +
                        state.sendGapShape.takeIf { it.size == 5 }?.let {
                            "\n<20 ${it[0]} · 20-50 ${it[1]} · 50-100 ${it[2]}" +
                                " · 100-200 ${it[3]} · >200 ${it[4]}"
                        }.orEmpty(),
                    style = Diagnostic,
                )
            }

            Button(onClick = { Producer.stopStream() }) { Text("Stop") }
        }
    }
}

/* -------------------------------------------------------------- rooms */

@Composable
private fun Rooms(state: Producer.State) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(
            Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text("Play on", style = MaterialTheme.typography.titleLarge)
            TextButton(onClick = { Producer.probe() }) { Text("Look again") }
        }

        if (state.destinations.isEmpty()) {
            Text(
                if (state.discovering) {
                    "No speakers yet. They have to be on this Wi-Fi — and if this " +
                        "phone is the hotspot, it has to be 2.4 GHz."
                } else {
                    "Not looking for speakers. Reopening the app is the quickest " +
                        "thing to try."
                },
                style = MaterialTheme.typography.bodyMedium,
            )
        }

        for (speaker in state.destinations) SpeakerCard(speaker)

        if (state.sources.isNotEmpty()) {
            Text("Already on the network", style = MaterialTheme.typography.titleLarge)
            for (source in state.sources) {
                SourceCard(source, enabled = state.selected.isNotEmpty())
            }
        }
    }
}

/* ------------------------------------------------------------- sources */

/**
 * "What would you like to hear?", as tiles.
 *
 * `play.html`'s question and `play.html`'s shape. A tile carries a mark, a
 * name and one line about what it is — never a paragraph. Everything that
 * used to be a paragraph here is now either in Settings or gone.
 */
@Composable
private fun Sources(state: Producer.State, onPickTrack: () -> Unit, onSpotify: () -> Unit) {
    var showStations by remember { mutableStateOf(false) }

    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text("What would you like to hear?", style = MaterialTheme.typography.titleLarge)

        /*
         * Four tiles, two rows. One row of four on a phone leaves each
         * about seventy pixels wide, which is a mark with a caption
         * squeezed under it rather than something anybody reads.
         */
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            SourceTile(
                glyph = Glyphs.Broadcast,
                name = "Spotify",
                what = if (state.signingIn) {
                    "Waiting for Spotify…"
                } else if (state.spotifySignedIn) {
                    "Publish \"${state.castName}\""
                } else {
                    "Sign in once, first"
                },
                enabled = !state.signingIn,
                onClick = onSpotify,
                modifier = Modifier.weight(1f),
            )
            SourceTile(
                glyph = Glyphs.MusicFile,
                name = "A music file",
                what = "From this phone.",
                onClick = onPickTrack,
                modifier = Modifier.weight(1f),
            )
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            SourceTile(
                glyph = Glyphs.Radio,
                name = "Radio",
                what = if (state.stations.isEmpty()) "Add a station" else
                    "${state.stations.size} saved",
                onClick = { showStations = !showStations },
                modifier = Modifier.weight(1f),
            )
            SourceTile(
                glyph = Glyphs.Tone,
                name = "Test tone",
                what = "Proves the wire.",
                onClick = { Producer.startStream(ToneSource()) },
                modifier = Modifier.weight(1f),
            )
        }

        if (showStations) Stations(state)

        if (state.signingIn) {
            Text(
                "Spotify's own page has opened. Take as long as you need — this " +
                    "waits, and no password is typed into this app.",
                style = MaterialTheme.typography.bodySmall,
            )
            OutlinedButton(onClick = { Producer.cancelSpotifySignIn() }) { Text("Cancel") }
        }
    }
}

/**
 * The saved stations, and a field to add one.
 *
 * Folded under the tile rather than given a screen of its own: this app is
 * one screen by design, and a station list that is four taps deep is a
 * list nobody edits at a party.
 */
@Composable
private fun Stations(state: Producer.State) {
    val context = LocalContext.current
    var name by remember { mutableStateOf("") }
    var url by remember { mutableStateOf("") }

    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        for (station in state.stations) {
            Card(Modifier.fillMaxWidth()) {
                Row(
                    Modifier.padding(12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    Icon(Glyphs.Radio, contentDescription = null, modifier = Modifier.size(24.dp))
                    Column(Modifier.weight(1f)) {
                        Text(station.name, style = MaterialTheme.typography.titleMedium)
                        Text(
                            station.url,
                            style = MaterialTheme.typography.bodySmall,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                    Button(onClick = { Producer.startStream(RadioSource(context, station)) }) {
                        Text("Play")
                    }
                    TextButton(onClick = { Producer.removeStation(context, station.id) }) {
                        Text("Remove")
                    }
                }
            }
        }

        OutlinedTextField(
            value = name,
            onValueChange = { name = it },
            label = { Text("Station name") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )
        OutlinedTextField(
            value = url,
            onValueChange = { url = it },
            label = { Text("Stream or playlist URL") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Button(
                onClick = {
                    Producer.addStation(context, name, url)
                    name = ""
                    url = ""
                },
                enabled = url.isNotBlank(),
            ) { Text("Add") }
        }
        Text(
            "MP3, AAC and FLAC. A .pls or .m3u address is fine — it is read " +
                "each time the station plays, so a station that moves its " +
                "stream keeps working.",
            style = MaterialTheme.typography.bodySmall,
        )
    }
}

@Composable
private fun SourceTile(
    glyph: ImageVector,
    name: String,
    what: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
) {
    Card(modifier.clickable(enabled = enabled, onClick = onClick)) {
        Column(
            Modifier.padding(12.dp).fillMaxWidth(),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            Icon(glyph, contentDescription = null, modifier = Modifier.size(32.dp))
            Text(
                name,
                style = MaterialTheme.typography.titleSmall,
                textAlign = TextAlign.Center,
            )
            Text(
                what,
                style = MaterialTheme.typography.bodySmall,
                textAlign = TextAlign.Center,
            )
        }
    }
}

/* ------------------------------------------------------------ settings */

/**
 * Folded away, because none of it is needed to play anything.
 *
 * The Hub does the same thing with a link at the foot of `play.html` —
 * "Setup and diagnostics … not needed to play anything".
 */
@Composable
private fun Settings(state: Producer.State) {
    val context = LocalContext.current
    var open by remember { mutableStateOf(false) }

    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        HorizontalDivider()
        Row(
            Modifier.fillMaxWidth().clickable { open = !open },
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text("Settings", style = MaterialTheme.typography.titleMedium)
            Text(if (open) "Hide" else "Show", style = MaterialTheme.typography.bodyMedium)
        }

        if (!open) return@Column

        /*
         * The name, editable, because everybody's device list is
         * different. Held locally while typing and committed on the way
         * out: writing on every keystroke would rename the device four
         * times while somebody spells a word.
         */
        var typed by remember(state.castName) { mutableStateOf(state.castName) }
        OutlinedTextField(
            value = typed,
            onValueChange = { typed = it },
            label = { Text("Name in Spotify and on the network") },
            singleLine = true,
            enabled = !state.streaming,
            modifier = Modifier.fillMaxWidth(),
        )
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Button(
                onClick = { Producer.setCastName(context, typed) },
                enabled = !state.streaming && typed != state.castName,
            ) { Text("Rename") }
            OutlinedButton(
                onClick = {
                    typed = Prefs.defaultCastName(context)
                    Producer.setCastName(context, typed)
                },
                enabled = !state.streaming,
            ) { Text("Default") }
        }
        Text(
            if (state.streaming) {
                "Stop first — a running cast point advertises the name it was " +
                    "started with, and there is no way to tell it otherwise."
            } else {
                "The \"${Prefs.PREFIX.trim()}\" prefix keeps every OpenAudioLink " +
                    "device together in Spotify's list. It is only a default."
            },
            style = MaterialTheme.typography.bodySmall,
        )

        Spacer(Modifier.height(4.dp))
        Text("Spotify account", style = MaterialTheme.typography.titleSmall)
        Text(
            if (state.spotifySignedIn) {
                "Signed in. The cast point belongs to that account, so forget it " +
                    "before handing this phone on."
            } else {
                "Not signed in. An unclaimed receiver is invisible in Spotify, " +
                    "however well it announces itself."
            },
            style = MaterialTheme.typography.bodySmall,
        )
        Text(
            "Needs Spotify Premium: librespot cannot stream on a free account, " +
                "though it will sign in on one and appear like an account that can.",
            style = MaterialTheme.typography.bodySmall,
        )
        OutlinedButton(onClick = { Producer.forgetSpotify(context) }) {
            Text(if (state.spotifySignedIn) "Forget account" else "Start over")
        }

        Spacer(Modifier.height(4.dp))
        Row(
            Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(Modifier.weight(1f)) {
                Text("Show details", style = MaterialTheme.typography.titleSmall)
                Text(
                    "Packet counters, the discovery heartbeat and librespot's own " +
                        "words. Off unless something needs explaining.",
                    style = MaterialTheme.typography.bodySmall,
                )
            }
            Switch(
                checked = state.showDetails,
                onCheckedChange = { Producer.setShowDetails(context, it) },
            )
        }
    }
}

/* ------------------------------------------------------------- details */

/**
 * Everything that used to be on the front page.
 *
 * Kept in full. Each of these lines was added because a screenshot could
 * not be read without it, and deleting them to tidy up would throw away
 * the ability to answer the next question — the fix is that they are no
 * longer the first thing anybody sees.
 */
@Composable
private fun Details(state: Producer.State) {
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        HorizontalDivider()
        Text("Details", style = MaterialTheme.typography.titleMedium)

        Text(
            "discovery: on ${state.listeningOn ?: "?"}" +
                (state.localAddress?.let { " ($it)" } ?: "") +
                " · heard ${state.datagramsHeard} · " +
                "announced ${state.announcesSent} · probes ${state.probesSent}" +
                (state.discoveryError?.let { " · $it" } ?: ""),
            style = Diagnostic,
        )

        if (state.streaming) {
            Text(
                if (state.sendingAudio) {
                    "sending: ${state.packetsSent} packets · " +
                        "${silence(state.underruns)} silence · ${state.resyncs} resyncs"
                } else {
                    // How long, not how many: a packet is 5 ms, and six
                    // figures of "held" reads like a fault rather than
                    // like waiting.
                    "waiting ${elapsed(state.packetsHeld)} · nothing sent"
                },
                style = Diagnostic,
            )

            /*
             * This end of the same measurement the nodes report.
             *
             * A node's arrival gaps cannot say whether the producer
             * stalled or the air clumped the packets; two of them agreeing
             * to within a fifth of a percent said the cause was upstream
             * of both, and this is the only place that can tell which.
             */
            if (state.sendingAudio) {
                Text(
                    "sent unevenly: ${state.sendGaps} gaps · worst " +
                        "${state.sendGapWorstMs} ms" +
                        state.sendGapShape.takeIf { it.size == 5 }?.let {
                            " · <20 ${it[0]} · 20-50 ${it[1]} · 50-100 ${it[2]}" +
                                " · 100-200 ${it[3]} · >200 ${it[4]}"
                        }.orEmpty(),
                    style = Diagnostic,
                )
            }
        }

        state.sourceReady?.let { ready ->
            Text(
                if (ready) {
                    "librespot: signed in to Spotify."
                } else {
                    "librespot has not signed in to Spotify yet."
                },
                style = MaterialTheme.typography.bodySmall,
            )
        }

        if (state.sourceLog.isNotEmpty()) {
            Text(
                state.sourceLog.takeLast(LOG_LINES_ON_SCREEN).joinToString("\n"),
                style = Diagnostic,
            )
        }

        if (state.others.isNotEmpty()) {
            Text(
                "also on the network, not driven from here: " +
                    state.others.joinToString { "${it.name} (${it.address})" },
                style = Diagnostic,
            )
        }

    }
}

/** How many of librespot's lines fit without pushing everything else off. */
private const val LOG_LINES_ON_SCREEN = 6

/**
 * Underruns as the time they represent, not the frames they are counted in.
 *
 * `PcmRing` counts frames, and a frame count sitting next to a packet
 * count invites exactly the comparison it cannot survive: "10451 packets ·
 * 17856 underruns" reads as more silence than audio, when it is 372 ms of
 * padding inside nearly a minute of music. Milliseconds are what the rest
 * of this system is measured in and what a person can weigh.
 */
private fun silence(frames: Long): String {
    val ms = frames / (Rtp.SAMPLE_RATE / 1000)
    return if (ms < 1000) "$ms ms" else "%.1f s".format(ms / 1000.0)
}

/** Held packets as the time they actually represent — 200 of them a second. */
private fun elapsed(packets: Long): String {
    val seconds = packets / (Rtp.SAMPLE_RATE / Rtp.FRAMES_PER_PACKET)
    return when {
        seconds < 60 -> "${seconds}s"
        seconds < 3600 -> "${seconds / 60}m ${seconds % 60}s"
        else -> "${seconds / 3600}h ${(seconds % 3600) / 60}m"
    }
}

/* --------------------------------------------------------------- cards */

@Composable
private fun SourceCard(source: Producer.Speaker, enabled: Boolean) {
    Card(Modifier.fillMaxWidth()) {
        Row(
            Modifier.padding(12.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Icon(Glyphs.Vinyl, contentDescription = null, modifier = Modifier.size(28.dp))
            Column(Modifier.weight(1f)) {
                Text(source.name, style = MaterialTheme.typography.titleMedium)
                Text(
                    "Makes its own sound. This phone only says where it goes.",
                    style = MaterialTheme.typography.bodySmall,
                )
            }
            Button(
                onClick = { Producer.startVinyl(source.id) },
                enabled = enabled,
            ) { Text("Play") }
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
                Icon(Glyphs.Speaker, contentDescription = null, modifier = Modifier.size(24.dp))
                Spacer(Modifier.size(10.dp))
                Column(Modifier.weight(1f)) {
                    Text(speaker.name, style = MaterialTheme.typography.titleMedium)
                    if (!speaker.online) {
                        /*
                         * Still ticked, still being sent to, and said so.
                         *
                         * This used to be a speaker silently vanishing
                         * from the list — which lost the tick with it, so
                         * "Look again" brought it back unselected and
                         * quiet. It stays now, because a run of lost
                         * announces is not a device leaving the house.
                         */
                        Text(
                            "Not answering — still sending, and asking again",
                            style = MaterialTheme.typography.bodySmall,
                        )
                    } else if (speaker.unreachable) {
                        /*
                         * Not the same sentence as "no volume control",
                         * and the difference cost a debugging session.
                         * "We have not heard back" is a question about
                         * this phone's reach; the other is a statement
                         * about the speaker, and saying it when the first
                         * is true blames working firmware.
                         */
                        Text(
                            "No answer yet",
                            style = MaterialTheme.typography.bodySmall,
                        )
                    }
                }
            }

            if (speaker.hasVolume) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Slider(
                        value = speaker.volume.toFloat(),
                        onValueChange = { Producer.setVolume(speaker.id, it.roundToInt()) },
                        valueRange = 0f..100f,
                        modifier = Modifier.weight(1f),
                    )
                    Spacer(Modifier.size(8.dp))
                    Text("${speaker.volume}", style = MaterialTheme.typography.bodySmall)
                }
            } else if (speaker.volumeUnsupported) {
                // Firmware older than 0.11.0 has no volume at all, and a
                // slider at zero would be a lie about a speaker playing
                // perfectly well.
                Text(
                    "This speaker's firmware has no volume control",
                    style = MaterialTheme.typography.bodySmall,
                )
            }

            if (!speaker.unreachable) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        "Room correction",
                        Modifier.weight(1f),
                        style = MaterialTheme.typography.bodyMedium,
                    )
                    Switch(
                        checked = speaker.roomCorrection,
                        onCheckedChange = { Producer.setRoomCorrection(speaker.id, it) },
                    )
                }
            }
        }
    }
}
