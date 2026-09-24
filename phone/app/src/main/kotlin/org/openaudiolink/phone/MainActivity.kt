package org.openaudiolink.phone

import android.Manifest
import android.content.Intent
import android.content.pm.ActivityInfo
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.OpenableColumns
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.BorderStroke
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
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
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
import org.openaudiolink.phone.sources.RadioSource
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

    /**
     * The system document picker, entered deliberately and rarely.
     *
     * It used to be what the "A music file" tile did, and that was a
     * one-way door: DocumentsUI is another app, it has no Cancel of its
     * own, and on a device driven by gestures there is no visible way back
     * out of it without choosing something. Nothing here could fix that —
     * it is not this app's window. What this app *can* do is stop putting
     * people in there for a file they have already played once, which is
     * what [Producer.tracks] and the list under the tile are for.
     *
     * Cancelling has always worked; the null branch below is the whole of
     * it. The problem was never that the app mishandled a cancel, it was
     * that the other app gave no way to ask for one.
     */
    private val pickTrack = registerForActivityResult(
        ActivityResultContracts.OpenDocument()
    ) { uri: Uri? ->
        if (uri != null) {
            /*
             * Persistable, and now actually used.
             *
             * This grant is what lets a URI survive a reboot, and it is
             * offered only to whoever received the result — so it has to be
             * taken here, before the URI goes anywhere else. The app has
             * always taken it and then thrown the URI away; the list is
             * what makes it worth taking.
             */
            contentResolver.takePersistableUriPermission(
                uri, Intent.FLAG_GRANT_READ_URI_PERMISSION
            )
            Producer.addTrack(this, nameOf(uri), uri)
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

        applyWallPanel()

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

    /**
     * Re-applied on every resume, because the setting can change while the
     * screen is up and a panel that only becomes a panel after a restart
     * is a setting people will think is broken.
     */
    override fun onResume() {
        super.onResume()
        applyWallPanel()
    }

    /**
     * What makes a screen a wall panel: landscape and lit.
     *
     * Two window properties and no separate build. Turning it off puts
     * both back, so the same APK is a phone in a pocket or a panel on a
     * wall depending on one switch.
     *
     * **Landscape** is locked rather than preferred, since a device
     * mounted on a wall has no way to be turned and its accelerometer will
     * happily decide otherwise.
     *
     * **Lit** is `FLAG_KEEP_SCREEN_ON` rather than a wake lock: it is
     * scoped to this window, so it lapses the moment the app is not in
     * front, and there is nothing to leak or forget to release.
     *
     * **There used to be a third.** It hid the status and navigation bars
     * and relied on an edge swipe to bring them back. That is the standard
     * kiosk move and it was the wrong default here: a device still being
     * set up needs its navigation, the swipe is a thing somebody has to be
     * told about, and hiding the clock and the battery on a panel whose
     * whole job is to be glanced at removes information rather than
     * clutter. Android's own bars are also how you leave, which matters
     * most on a device you have owned for an hour.
     *
     * If a full kiosk is wanted later it belongs with lock-task mode and
     * device owner, where leaving is a deliberate act with a way back —
     * not with three window flags and a gesture.
     */
    private fun applyWallPanel() {
        val panel = Prefs.wallPanel(this)

        requestedOrientation = if (panel) {
            ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE
        } else {
            ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
        }

        if (panel) {
            window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        } else {
            window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        }
    }

    /**
     * What the file is called, asked of the provider rather than guessed.
     *
     * This used to read the last path segment, which for a document URI is
     * an opaque id — `audio%3A1000000123` — so every picked file was
     * labelled with a number. That was survivable while the name was only
     * a banner caption on a track playing right now; it is not survivable
     * in a saved list, where the name is the only thing telling two rows
     * apart.
     *
     * `DISPLAY_NAME` is the one column every document provider is required
     * to answer, and it gives the file's real name. A provider that answers
     * with nothing leaves the fallback in [Track] to salvage something.
     */
    private fun nameOf(uri: Uri): String {
        try {
            contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)
                ?.use { cursor ->
                    val column = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                    if (column >= 0 && cursor.moveToFirst()) {
                        cursor.getString(column)?.takeIf { it.isNotBlank() }?.let { return it }
                    }
                }
        } catch (_: Exception) {
            // A provider that will not answer a name query is not a reason
            // to refuse the file it just handed over.
        }
        return ""
    }
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

        /*
         * Shown while something is playing, too.
         *
         * This used to be hidden the moment a stream started, so the only
         * way to look at another source was to press Stop first — which
         * silenced the room to answer the question "what else is there?".
         * Browsing is not choosing: opening a panel changes nothing, and
         * the music keeps playing until an actual Play is pressed.
         */
        Sources(
            state = state,
            onPickTrack = onPickTrack,
            /*
             * Signing in is the only thing Spotify needs a button for.
             *
             * Once an account is attached the cast point publishes itself
             * and stays published; after that, playing is something done
             * in Spotify on whichever phone has the music, and this screen
             * only reports it.
             */
            onSignIn = {
                Producer.signInToSpotify(context, state.castName) { url ->
                    context.startActivity(
                        Intent(Intent.ACTION_VIEW, Uri.parse(url))
                            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                    )
                }
            },
        )

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
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            CastPointLamp(state)

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
                        state.destinations.size == 1 -> "1 speaker"
                        else -> "${state.destinations.size} speakers"
                    },
                    style = MaterialTheme.typography.bodySmall,
                )
            }
        }
    }
}

/**
 * Whether this device is offering itself to Spotify, as a standing lamp.
 *
 * **It replaces an announcement.** Signing in used to raise a full-width
 * card saying the device was now in Spotify's list and would stay there —
 * a sentence delivered once, to whoever happened to be holding the tablet,
 * and then dismissed for ever. That is the wrong shape for a fact that
 * remains true all evening and that anybody walking past might want to
 * check. A lamp is either lit or it is not.
 *
 * Spotify's own device list is the model: a screen outline, the name
 * beside it, and a line underneath saying what that device is doing. The
 * convention is borrowed; the glyph is drawn from scratch, because the
 * mark in that list is a trademark and this project has no licence to it.
 *
 * Shown only when the cast point is actually up, so it means something by
 * being present. Absent covers three different situations — no account,
 * the switch off, librespot not yet started — and the Spotify panel is
 * where those are told apart, because a lamp that tries to explain itself
 * is a paragraph again.
 */
@Composable
private fun CastPointLamp(state: Producer.State) {
    if (!state.castPointUp) return

    val casting = state.castPointCasting

    /*
     * Lit, not merely present.
     *
     * The first version drew the whole thing in `onSurfaceVariant` unless
     * somebody was casting, which made the ordinary state — published and
     * waiting, which is what it is nearly all the time — render as muted
     * grey. Grey on a dark panel is the colour of something switched off,
     * so the indicator for "this is available" looked like the indicator
     * for "this is not".
     *
     * Available is now genuinely lit: the mark in the accent, the name at
     * full strength, on a panel of its own so it reads as an instrument
     * rather than a caption. Casting adds an accent outline and turns the
     * status word accent too, so there is still a clear difference between
     * offered and in use — two degrees of on, rather than off and on.
     */
    Surface(
        shape = MaterialTheme.shapes.medium,
        color = MaterialTheme.colorScheme.surface,
        border = if (casting) {
            BorderStroke(1.dp, MaterialTheme.colorScheme.primary)
        } else {
            null
        },
    ) {
        Row(
            Modifier.padding(horizontal = 10.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Icon(
                Glyphs.CastPoint,
                contentDescription = null,
                modifier = Modifier.size(24.dp),
                tint = MaterialTheme.colorScheme.primary,
            )
            Column(horizontalAlignment = Alignment.Start) {
                Text(
                    state.castName,
                    style = MaterialTheme.typography.bodyMedium,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    if (casting) "Casting" else "In Spotify",
                    style = MaterialTheme.typography.bodySmall,
                    color = if (casting) {
                        MaterialTheme.colorScheme.primary
                    } else {
                        MaterialTheme.colorScheme.onSurfaceVariant
                    },
                )
            }
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
            /*
             * The track, where anything can say what it is; the source
             * otherwise.
             *
             * A wall panel is read from across a room, and "Spotify — OAL
             * Phone" tells whoever is standing there nothing they did not
             * know. Radio reports a title through ICY and Spotify through
             * librespot's own log, so the banner leads with that and puts
             * the source underneath, where it becomes context rather than
             * the headline.
             *
             * Falls back silently. A test tone, a vinyl node, a local file
             * and a station that sends no metadata all have nothing to say
             * here, which is ordinary rather than a failure and should not
             * read as one.
             */
            val playing = state.nowPlaying
            Text(
                playing?.title ?: state.sourceLabel ?: "",
                style = MaterialTheme.typography.titleMedium,
            )
            playing?.artist?.takeIf { it.isNotBlank() }?.let { artist ->
                Text(artist, style = MaterialTheme.typography.bodyMedium)
            }
            if (playing != null) {
                // Station and source together: "Radio Paradise · Internet
                // radio" says both which station and which of the five
                // sources this is, and neither alone does.
                val where = listOfNotNull(
                    playing.station?.takeIf { it != playing.title },
                    state.sourceLabel,
                ).distinct().joinToString(" · ")
                if (where.isNotBlank()) {
                    Text(
                        where,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
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

            Button(onClick = { Producer.stopPlayback() }) { Text("Stop") }
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
                    /*
                     * A subnet, not a band — and the difference is not
                     * pedantry, it is the arrangement this project
                     * recommends.
                     *
                     * This used to say "they have to be on this Wi-Fi",
                     * which is narrower than the truth and contradicted
                     * the project's own measurements: Run 43 in
                     * LINK-MEASUREMENTS is a phone producer on 5 GHz
                     * driving speakers on 2.4 GHz, and it matched a wired
                     * Hub. Splitting the bands is *better* here, because
                     * the producer's uplink is the hop that showed up in
                     * the numbers and the ESP32-S3's radio has no say in
                     * the matter.
                     *
                     * What actually matters is that discovery is a
                     * multicast announce, and multicast does not cross a
                     * subnet boundary. Bridged bands are one network;
                     * routed ones — a guest SSID, a separate IoT network,
                     * some mesh defaults — are two, and no amount of
                     * waiting will join them.
                     */
                    "No speakers yet. They have to share a subnet with this device " +
                        "rather than a band: speakers on 2.4 GHz while this is on " +
                        "5 GHz is fine, and is the better arrangement, so long as " +
                        "the router bridges the two rather than routing them apart. " +
                        "If this device is the hotspot it has to be 2.4 GHz, " +
                        "because the speakers' radio is."
                } else {
                    "Not looking for speakers. Reopening the app is the quickest " +
                        "thing to try."
                },
                style = MaterialTheme.typography.bodyMedium,
            )
        }

        /*
         * Speakers only.
         *
         * A turntable node used to be listed here too, under "Already on
         * the network", which put a *source* in the section headed "Play
         * on" — the one part of the screen that answers where the sound
         * goes rather than where it comes from. It has its own tile now,
         * with the other four things somebody might want to hear.
         */
        for (speaker in state.destinations) SpeakerCard(speaker)
    }
}

/* ------------------------------------------------------------- sources */

/**
 * Which of the four tiles is open.
 *
 * One value rather than a boolean per tile, and that is the whole fix for
 * what was wrong here. Radio and A music file each had their own switch,
 * so both panels could be open at once and the space under the tiles
 * became a station list and a file list stacked together with nothing
 * saying which belonged to which. Two independent booleans describe four
 * states, and three of them were wrong.
 *
 * `null` is "nothing open", which is what the screen starts as and what
 * tapping the open tile again returns to.
 */
private enum class Panel { SPOTIFY, FILE, RADIO, VINYL }

/** Tap the open one to close it; tap another to move there. */
private fun Panel?.toggle(tapped: Panel): Panel? = if (this == tapped) null else tapped

/**
 * "What would you like to hear?", as tiles, with one panel under them.
 *
 * `play.html`'s question and `play.html`'s shape. A tile carries a mark, a
 * name and one line about what it is — never a paragraph. The paragraphs
 * live in the panel that tile opens.
 *
 * **Every tile behaves the same way**, which is the second thing that was
 * wrong. Radio and A music file opened a panel; Spotify and Test tone
 * started playing on the spot. So two of the four tiles were questions and
 * two were commands, they looked identical, and the only way to find out
 * which was which was to press one. Now all four open, and the thing that
 * starts audio is always a Play button inside.
 *
 * That also means **looking costs nothing**. This whole block used to
 * vanish while a stream was running, so changing your mind meant pressing
 * Stop to see what else there was. Now the tiles stay, the panels open and
 * close over a playing stream, and the music stops only when something
 * else is actually started.
 */
@Composable
private fun Sources(state: Producer.State, onPickTrack: () -> Unit, onSignIn: () -> Unit) {
    var panel by remember { mutableStateOf<Panel?>(null) }

    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(
            if (state.streaming) "Play something else" else "What would you like to hear?",
            style = MaterialTheme.typography.titleLarge,
        )

        /*
         * Four tiles, two rows. One row of four on a handset leaves each
         * about seventy pixels wide, which is a mark with a caption
         * squeezed under it rather than something anybody reads.
         */
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            SourceTile(
                glyph = Glyphs.Broadcast,
                name = "Spotify",
                /*
                 * The three states Spotify has, said in one line.
                 *
                 * No account, available, playing — and "available" is the
                 * one that used not to exist. It is also the one the whole
                 * sticky cast point is for: the device sitting in
                 * everybody's Spotify all evening, costing nothing, until
                 * somebody picks it.
                 */
                what = when {
                    state.signingIn -> "Waiting for Spotify…"
                    !state.spotifySignedIn -> "Sign in once, first"
                    state.castPointCasting -> "Playing"
                    state.castPointUp -> "Available"
                    else -> "Not published"
                },
                selected = panel == Panel.SPOTIFY,
                onClick = { panel = panel.toggle(Panel.SPOTIFY) },
                modifier = Modifier.weight(1f),
            )
            /*
             * Opens a list, not the system picker.
             *
             * This tile used to launch Android's document browser directly,
             * which meant every play went through another app — and that
             * app has no Cancel, so on a device with gesture navigation
             * tapping this tile by mistake left somebody stuck in a file
             * browser they could only leave by choosing a file.
             */
            SourceTile(
                glyph = Glyphs.MusicFile,
                name = "A music file",
                what = if (state.tracks.isEmpty()) "From this device" else
                    "${state.tracks.size} remembered",
                selected = panel == Panel.FILE,
                onClick = { panel = panel.toggle(Panel.FILE) },
                modifier = Modifier.weight(1f),
            )
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            SourceTile(
                glyph = Glyphs.Radio,
                name = "Radio",
                what = if (state.stations.isEmpty()) "Add a station" else
                    "${state.stations.size} saved",
                selected = panel == Panel.RADIO,
                onClick = { panel = panel.toggle(Panel.RADIO) },
                modifier = Modifier.weight(1f),
            )
            /*
             * The turntable, finally where somebody would look for it.
             *
             * It is the one source this app does not produce: a node with
             * a pickup on it makes its own sound and is merely *told* where
             * to send it, so for a long time it was listed under the
             * speakers as "already on the network". That put a source in
             * the section headed "Play on", which answers the opposite
             * question, and meant the analogue input — the reason half of
             * this project exists — was the one thing not on offer when the
             * screen asked what you would like to hear.
             */
            SourceTile(
                glyph = Glyphs.Vinyl,
                name = "Vinyl",
                what = when (state.sources.size) {
                    0 -> "None found"
                    1 -> "1 turntable"
                    else -> "${state.sources.size} turntables"
                },
                selected = panel == Panel.VINYL,
                onClick = { panel = panel.toggle(Panel.VINYL) },
                modifier = Modifier.weight(1f),
            )
        }

        /*
         * Exactly one, or none. A `when` on one value cannot draw two.
         */
        when (panel) {
            Panel.SPOTIFY -> Spotify(state, onSignIn)
            Panel.FILE -> Tracks(state, onPickTrack)
            Panel.RADIO -> Stations(state)
            Panel.VINYL -> Vinyl(state)
            null -> Unit
        }
    }
}

/**
 * The cast point, and the one button that publishes it.
 *
 * Everything here used to be squeezed onto the tile's single caption line
 * or dropped underneath all four tiles, where the sign-in explanation
 * appeared with nothing tying it to the tile it belonged to.
 */
@Composable
private fun Spotify(state: Producer.State, onSignIn: () -> Unit) {
    val context = LocalContext.current

    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        if (!state.spotifySignedIn) {
            Text(
                "Signs in once, in Spotify's own page. No password is typed into " +
                    "this app, and Premium is required — librespot cannot stream " +
                    "on a free account.",
                style = MaterialTheme.typography.bodySmall,
            )
            Button(onClick = onSignIn, enabled = !state.signingIn) { Text("Sign in") }

            if (state.signingIn) {
                Text(
                    "Spotify's own page has opened. Take as long as you need — " +
                        "this waits.",
                    style = MaterialTheme.typography.bodySmall,
                )
                OutlinedButton(onClick = { Producer.cancelSpotifySignIn() }) { Text("Cancel") }
            }
            return@Column
        }

        /*
         * Signed in: what the cast point is doing, in one sentence.
         *
         * There is deliberately no Publish button in the ordinary case.
         * The whole point of the sticky cast point is that nobody has to
         * press anything here for the device to be in Spotify's list —
         * requiring a press first is what made a guest unable to discover
         * it at all.
         */
        Text(
            when {
                state.castPointCasting ->
                    "Somebody is casting to \"${state.castName}\"."
                state.castPointUp ->
                    "In Spotify as \"${state.castName}\", waiting. Pick it on any " +
                        "phone signed into this account and press play — nothing " +
                        "needs pressing here."
                state.castPointOn ->
                    "Not on the network yet. It comes back on its own; if it does " +
                        "not, Show details has librespot's own words."
                else ->
                    "Switched off, so \"${state.castName}\" is not in anybody's " +
                        "Spotify."
            },
            style = MaterialTheme.typography.bodySmall,
        )

        Row(
            Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(Modifier.weight(1f)) {
                Text("Available in Spotify", style = MaterialTheme.typography.titleSmall)
                /*
                 * The reason to turn it off, stated rather than implied.
                 *
                 * This is the one switch in the app that changes who else
                 * can make noise in the house, so it says so.
                 */
                Text(
                    "While this is on, anyone on this network signed into the " +
                        "same Spotify account can play here without touching this " +
                        "device.",
                    style = MaterialTheme.typography.bodySmall,
                )
            }
            Switch(
                checked = state.castPointOn,
                onCheckedChange = { Producer.setCastPoint(context, it) },
            )
        }

        /*
         * No Play button, deliberately.
         *
         * There was one, to override the few seconds after a choice at the
         * panel during which an arriving cast is ignored. It was answering
         * a question nobody asks: with the cast point published, playing
         * Spotify here is something you do *in Spotify*, on whichever
         * phone has the music. Pressing a button on the tablet when nobody
         * is casting merely opens a stream that sends silence — the
         * "publish and wait" state the sticky cast point exists to abolish.
         *
         * And the case it was for resolves itself: the watcher re-checks
         * every second, so a cast that arrives inside the guard window is
         * picked up as soon as the window closes.
         */
    }
}

/**
 * The turntables on the network, and where to send each one.
 *
 * **The only source this app does not produce.** A node with a pickup on
 * it digitises its own audio and sends its own RTP; this phone is its
 * Controller, not its producer, so "Play" here is one HTTP request telling
 * it which speakers to send to — decision 19's split, on screen. Nothing
 * flows through this device at all, which is why the packet counters stay
 * at zero while a record is playing perfectly.
 *
 * It needs speakers ticked before it can be told anything, since the whole
 * request is a destination list. That is the one case where a Play button
 * here is disabled rather than merely unhelpful.
 */
@Composable
private fun Vinyl(state: Producer.State) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        if (state.sources.isEmpty()) {
            Text(
                "No turntable on the network. A node set up as a producer — one " +
                    "with a pickup or a line input wired to it — appears here by " +
                    "itself, with no setting to change on this device.",
                style = MaterialTheme.typography.bodySmall,
            )
            return@Column
        }

        for (source in state.sources) {
            SourceCard(source, enabled = state.selected.isNotEmpty())
        }

        Text(
            if (state.selected.isEmpty()) {
                "Tick a speaker first. Playing a turntable is telling it where to " +
                    "send, so with nothing ticked there is nothing to tell it."
            } else {
                "It plays itself and this device only says where the sound goes, " +
                    "so the counters under Show details stay at zero while it runs."
            },
            style = MaterialTheme.typography.bodySmall,
        )
    }
}

/**
 * The files played before, and the one button that opens the picker.
 *
 * [Stations]'s shape deliberately — a row per thing with Play and Remove,
 * and one control at the foot for adding another — because it is the same
 * job and nobody should have to learn this screen twice.
 *
 * What differs is what "add" costs. A station is two fields typed here; a
 * file means handing the screen to Android's document browser, which is
 * another app with its own back behaviour and no Cancel. So the button
 * that does it says so, and it is at the bottom rather than being what the
 * tile does.
 *
 * **Newest first.** Playing from this list does not reorder it — only
 * picking a file does — because a list that rearranges itself under a
 * finger is a list people mis-tap.
 */
@Composable
private fun Tracks(state: Producer.State, onPickTrack: () -> Unit) {
    val context = LocalContext.current

    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        for (track in state.tracks) {
            Card(Modifier.fillMaxWidth()) {
                Row(
                    Modifier.padding(12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    Icon(
                        Glyphs.MusicFile,
                        contentDescription = null,
                        modifier = Modifier.size(24.dp),
                    )
                    Text(
                        track.name,
                        Modifier.weight(1f),
                        style = MaterialTheme.typography.titleMedium,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )
                    Button(onClick = { Producer.playTrack(context, track) }) { Text("Play") }
                    TextButton(onClick = { Producer.removeTrack(context, track.id) }) {
                        Text("Remove")
                    }
                }
            }
        }

        Button(onClick = onPickTrack) { Text("Choose a file…") }

        Text(
            if (state.tracks.isEmpty()) {
                "Nothing yet. Choosing a file opens this device's own file browser — " +
                    "which has no way out but picking something, so what is picked " +
                    "is remembered here and needs choosing only once."
            } else {
                "Remembered, not copied: the audio stays where it is. A file that " +
                    "has since been deleted or moved will publish and then play " +
                    "nothing — Remove is the way out of that."
            },
            style = MaterialTheme.typography.bodySmall,
        )
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

/**
 * One tile, and whether its panel is the one open.
 *
 * [selected] is filled rather than outlined, because an outline on a Card
 * that already has an edge is a difference people have to look for. The
 * colour is the banner's, so "this is the thing you are looking at" reads
 * the same in both places.
 */
@Composable
private fun SourceTile(
    glyph: ImageVector,
    name: String,
    what: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    selected: Boolean = false,
) {
    /*
     * Always clickable. The tile used to be disabled while Spotify's
     * sign-in was in flight, which made the one tile you wanted to look at
     * the one tile you could not open. Opening a panel is safe at any
     * moment; it is the button inside that knows when it cannot act.
     */
    /*
     * Selected is a border, not a fill, and the fill is why.
     *
     * This used to swap the container to `secondaryContainer`, which the
     * theme maps to `Oal.Panel2` — and so does `surfaceVariant`, while a
     * Card's own default container is derived a shade lighter. So the
     * *selected* tile came out darker than the three beside it, which
     * reads as disabled rather than chosen: the one tile being looked at
     * was the one that appeared switched off.
     *
     * A border in the accent settles it without depending on which of two
     * greys is lighter, and it is legible from across a room, which a
     * three-percent difference in fill is not. The theme is dark only, so
     * there is no second scheme for this to be wrong in.
     */
    Card(
        modifier.clickable(onClick = onClick),
        colors = CardDefaults.cardColors(),
        border = if (selected) {
            BorderStroke(2.dp, MaterialTheme.colorScheme.primary)
        } else {
            null
        },
    ) {
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
                    "before handing this device on."
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
                Text("Wall panel", style = MaterialTheme.typography.titleSmall)
                Text(
                    "Landscape and the screen always on, for a device that is " +
                        "mounted rather than carried.",
                    style = MaterialTheme.typography.bodySmall,
                )
            }
            Switch(
                checked = state.wallPanel,
                onCheckedChange = { Producer.setWallPanel(context, it) },
            )
        }

        /*
         * The test tone, out of the tiles and in here.
         *
         * It was a quarter of the screen's "what would you like to hear",
         * and nobody has ever wanted to hear it. It answers a question
         * instead — is the network right — by being the one source with no
         * account, no file and no internet behind it: if the speakers play
         * this and nothing else, the fault is upstream of the wire rather
         * than in it. That is a diagnostic, and diagnostics belong with the
         * counters and the log rather than beside Spotify.
         *
         * Moving it also gave the vinyl node the tile it had never had,
         * which is the better use of that square: a turntable is something
         * somebody actually wants to listen to.
         */
        Spacer(Modifier.height(4.dp))
        Row(
            Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(Modifier.weight(1f)) {
                Text("Test tone", style = MaterialTheme.typography.titleSmall)
                Text(
                    "Proves the wire. No account, no file and no internet, so if " +
                        "this plays and nothing else does, the fault is in the " +
                        "source rather than the network.",
                    style = MaterialTheme.typography.bodySmall,
                )
            }
            Button(onClick = { Producer.startStream(ToneSource()) }) { Text("Play") }
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
            /*
             * What is being done right now, which is not the same question
             * as what this stream has done.
             */
            if (!state.sendingAudio) {
                // How long, not how many: a packet is 5 ms, and six
                // figures of "held" reads like a fault rather than
                // like waiting.
                Text("waiting ${elapsed(state.packetsHeld)} · nothing sent", style = Diagnostic)
            }

            /*
             * The totals, shown whenever there are any — **not** only
             * while audio is flowing.
             *
             * Gating these on `sendingAudio` threw the record of a run
             * away at the exact moment it became worth reading. A Spotify
             * queue that empties overnight leaves the gate holding, so a
             * screen checked in the morning said "waiting 4h 45m · nothing
             * sent" and nothing else: no packet count, no gap histogram,
             * for a five-hour stream that had just finished. The Hub's
             * sample log had recorded seven producer-side stalls that
             * night and this line is the only thing that can confirm them
             * from the sending end — and it had erased itself.
             *
             * A stream that has sent nothing at all still shows nothing,
             * which is the case the gate was really for.
             */
            if (state.packetsSent > 0) {
                Text(
                    "sent: ${state.packetsSent} packets · " +
                        "${silence(state.underruns)} silence · ${state.resyncs} resyncs",
                    style = Diagnostic,
                )

                /*
                 * This end of the same measurement the nodes report.
                 *
                 * A node's arrival gaps cannot say whether the producer
                 * stalled or the air clumped the packets; two of them
                 * agreeing to within a fifth of a percent said the cause
                 * was upstream of both, and this is the only place that
                 * can tell which.
                 */
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
                source.fw.takeIf { it.isNotBlank() }?.let { version ->
                    Text(
                        "v$version",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                Text(
                    "Makes its own sound. This device only says where it goes.",
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

@OptIn(ExperimentalMaterial3Api::class)
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
                    /*
                     * The firmware, under the name, exactly as this app
                     * prints its own version under its own name.
                     *
                     * Two reasons beyond symmetry. A house accumulates
                     * nodes flashed at different times and there is
                     * otherwise nowhere to see which is behind. And
                     * `protocol/OTA.md` warns that an update to the
                     * version already installed completes, reboots and
                     * changes nothing — which cannot be told from a
                     * successful update unless the version is on screen.
                     */
                    speaker.fw.takeIf { it.isNotBlank() }?.let { version ->
                        Text(
                            "v$version",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
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

            /*
             * Volume and room correction on one line, not two.
             *
             * Room correction used to have a row of its own: the words on
             * the far left, a full-size Switch on the far right, and on a
             * 1340 px tablet a thousand pixels of nothing between them.
             * That gave it the same visual weight as the volume — and a
             * switch on its own row reads as a setting somebody is
             * expected to decide, when it is an occasional per-speaker
             * preference that is usually just left alone.
             *
             * A chip is the right shape for that: compact, optional by
             * convention, and it takes the space at the end of the volume
             * line that was empty anyway. It also takes a row off every
             * speaker card, which is worth having on a screen that has to
             * be scrolled to reach the sources.
             */
            if (speaker.hasVolume || speaker.volumeUnsupported || !speaker.unreachable) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    if (speaker.hasVolume) {
                        Slider(
                            value = speaker.volume.toFloat(),
                            onValueChange = { Producer.setVolume(speaker.id, it.roundToInt()) },
                            valueRange = 0f..100f,
                            modifier = Modifier.weight(1f),
                        )
                        Text("${speaker.volume}", style = MaterialTheme.typography.bodySmall)
                    } else if (speaker.volumeUnsupported) {
                        // Firmware older than 0.11.0 has no volume at all,
                        // and a slider at zero would be a lie about a
                        // speaker playing perfectly well.
                        Text(
                            "This speaker's firmware has no volume control",
                            Modifier.weight(1f),
                            style = MaterialTheme.typography.bodySmall,
                        )
                    } else {
                        Spacer(Modifier.weight(1f))
                    }

                    if (!speaker.unreachable) {
                        /*
                         * The state is in the words as well as in the fill.
                         *
                         * A selected chip differs from an unselected one by
                         * container colour, and in dark mode that
                         * difference is slight — the same weakness the
                         * selected source tile has. A panel on a wall is
                         * read from across a room and at a glance, so the
                         * label says which way it is set rather than
                         * leaving it to a shade of grey.
                         */
                        FilterChip(
                            selected = speaker.roomCorrection,
                            onClick = {
                                Producer.setRoomCorrection(
                                    speaker.id, !speaker.roomCorrection
                                )
                            },
                            label = {
                                Text(
                                    if (speaker.roomCorrection) {
                                        "Room correction on"
                                    } else {
                                        "Room correction off"
                                    }
                                )
                            },
                        )
                    }
                }
            }
        }
    }
}
