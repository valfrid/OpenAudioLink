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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import org.openaudiolink.phone.sources.LibrarySource
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
        ProducerService.start(this)

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
    }

    private fun nameOf(uri: Uri): String =
        uri.lastPathSegment?.substringAfterLast('/') ?: "Track"
}

@Composable
private fun Screen(modifier: Modifier = Modifier, onPickTrack: () -> Unit) {
    val state by Producer.state.collectAsState()

    Column(modifier.fillMaxSize().padding(16.dp)) {

        state.warning?.let { warning ->
            Card(Modifier.fillMaxWidth().padding(bottom = 12.dp)) {
                Column(Modifier.padding(12.dp)) {
                    Text(warning, style = MaterialTheme.typography.bodyMedium)
                    TextButton(onClick = { Producer.dismissWarning() }) { Text("Dismiss") }
                }
            }
        }

        Text("Play from this phone", style = MaterialTheme.typography.titleLarge)
        Spacer(Modifier.height(8.dp))

        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            if (state.streaming) {
                Button(onClick = { Producer.stopStream() }) { Text("Stop") }
                Text(
                    state.sourceLabel ?: "",
                    Modifier.align(Alignment.CenterVertically),
                    style = MaterialTheme.typography.bodyMedium,
                )
            } else {
                Button(onClick = onPickTrack) { Text("Choose a track") }
                // Needs no permission, no file and no account, so it
                // separates "is the network right" from "is the decoder
                // right" when a speaker is silent.
                OutlinedButton(onClick = { Producer.startStream(ToneSource()) }) {
                    Text("Test tone")
                }
            }
        }

        if (state.streaming) {
            Spacer(Modifier.height(4.dp))
            Text(
                "${state.packetsSent} packets · ${state.underruns} underruns · " +
                    "${state.resyncs} resyncs",
                style = MaterialTheme.typography.bodySmall,
            )
        }

        Spacer(Modifier.height(20.dp))
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
            Text("Speakers", style = MaterialTheme.typography.titleLarge)
            TextButton(onClick = { Producer.probe() }) { Text("Look again") }
        }

        if (state.speakers.isEmpty()) {
            Text(
                "None yet. They must be on this Wi-Fi — if you are hosting the " +
                    "hotspot, it has to be 2.4 GHz, because the speakers' radio " +
                    "cannot see 5 GHz at all.",
                style = MaterialTheme.typography.bodyMedium,
            )
        }

        LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            items(state.speakers, key = { it.id }) { speaker ->
                SpeakerCard(speaker, streaming = state.streaming)
            }
        }
    }
}

@Composable
private fun SpeakerCard(speaker: Producer.Speaker, streaming: Boolean) {
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
            } else {
                // Firmware older than 0.11.0 has no volume at all, and a
                // slider at zero would be a lie about a speaker playing
                // perfectly well.
                Text("This speaker's firmware has no volume control",
                    style = MaterialTheme.typography.bodySmall)
            }

            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Room correction", Modifier.weight(1f),
                    style = MaterialTheme.typography.bodyMedium)
                Switch(
                    checked = speaker.roomCorrection,
                    onCheckedChange = { Producer.setRoomCorrection(speaker.id, it) },
                )
            }

            /*
             * The turntable. The phone is its Controller, not its source:
             * the node is already a Producer, so it is told where to send
             * rather than asked to hand over the audio.
             */
            if (speaker.isProducerNode) {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(
                        onClick = { Producer.startVinyl(speaker.id) },
                        enabled = !streaming,
                    ) { Text("Play this source") }
                    OutlinedButton(onClick = { Producer.stopVinyl(speaker.id) }) {
                        Text("Stop it")
                    }
                }
            }
        }
    }
}
