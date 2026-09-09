package org.openaudiolink.phone

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/**
 * A test card for the hollow-glyph fault, because guessing cost four builds.
 *
 * Some text on this app's screen draws as dark glyphs with a light edge
 * behind them rather than as solid letters, and the lines it happens to
 * are mixed in among lines it does not: a text field's label is hollow
 * while the value in the same field is solid. Four versions tried to
 * explain that from a property of the text — a monospace family (0.8.2),
 * `fontFeatureSettings = "tnum"` (0.8.3, out in 0.8.6), then bundling
 * Roboto and pinning every weight (0.8.7). None of them changed it, and
 * each cost a build, an install and a screenshot to learn one bit.
 *
 * That is a bad way to find a fault. Reading a screenshot of the *real*
 * screen means comparing lines that differ in five things at once and
 * arguing about which one matters. This screen holds everything constant
 * and varies **one property per line**, each with a caption saying what
 * it varied. A single screenshot of it names the trigger outright.
 *
 * The groups, and what each rules out:
 *
 * - **colour** — the strongest correlation so far. Every hollow line on
 *   the real screen is muted grey, mint, or dark-on-mint; every solid one
 *   is near-white. If only the muted rows are hollow here, it is colour
 *   and nothing else.
 * - **family** — 0.8.7 bundled Roboto, so if `default` and `sans` are
 *   hollow and `bundled` is not, the system font is still reaching the
 *   screen somewhere.
 * - **size** and **weight** — both were suspected and neither survived
 *   contact with the real screen, where `bodySmall` and `labelLarge` each
 *   appear on both sides of the split. Held here for completeness.
 * - **background** — the same near-white text on all three surfaces. The
 *   volume numbers are hollow on a card and solid on the ground, which
 *   this either reproduces or kills.
 * - **widget** — plain `Text` against the three button kinds. Buttons are
 *   hollow on the real screen while a plain line of the same style is
 *   not, so if the sample text is solid and the button labels are not,
 *   the cause is in how a button draws rather than in the text at all.
 *
 * Delete this file once the fault is found. It is a diagnostic, not a
 * feature, and it lives behind *Show details* so it costs an ordinary
 * user nothing in the meantime.
 */
private const val SAMPLE = "Hg 0123 wlan0"

@Composable
private fun Probe(caption: String, sample: @Composable () -> Unit) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        /*
         * The caption is deliberately in the style that is known to draw
         * correctly — near-white, bundled family. If the captions
         * themselves come out hollow, that is worth knowing too, and it
         * would mean the split is not a property of the text at all.
         */
        Text(
            caption,
            Modifier.width(104.dp),
            style = MaterialTheme.typography.bodySmall,
            color = Oal.Text,
        )
        sample()
    }
}

@Composable
fun FontProbe() {
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text("Font test", style = MaterialTheme.typography.titleMedium)
        Text(
            "One property changes per line. Which lines are hollow?",
            style = MaterialTheme.typography.bodySmall,
            color = Oal.Text,
        )

        Text("colour", style = MaterialTheme.typography.titleSmall)
        Probe("text") { Text(SAMPLE, color = Oal.Text) }
        Probe("muted") { Text(SAMPLE, color = Oal.Muted) }
        Probe("accent") { Text(SAMPLE, color = Oal.Accent) }
        Probe("pure white") { Text(SAMPLE, color = Color.White) }
        Probe("50% grey") { Text(SAMPLE, color = Color(0xFF808080)) }

        // Colour held at a value that is hollow on the real screen, so a
        // family that draws it solid here is the family to move to.
        Text("family, all muted", style = MaterialTheme.typography.titleSmall)
        Probe("bundled") { Text(SAMPLE, color = Oal.Muted, fontFamily = OalRoboto) }
        Probe("default") { Text(SAMPLE, color = Oal.Muted, fontFamily = FontFamily.Default) }
        Probe("sans") { Text(SAMPLE, color = Oal.Muted, fontFamily = FontFamily.SansSerif) }
        Probe("serif") { Text(SAMPLE, color = Oal.Muted, fontFamily = FontFamily.Serif) }
        Probe("monospace") { Text(SAMPLE, color = Oal.Muted, fontFamily = FontFamily.Monospace) }

        Text("size, all muted", style = MaterialTheme.typography.titleSmall)
        Probe("12sp") { Text(SAMPLE, color = Oal.Muted, fontSize = 12.sp) }
        Probe("16sp") { Text(SAMPLE, color = Oal.Muted, fontSize = 16.sp) }
        Probe("22sp") { Text(SAMPLE, color = Oal.Muted, fontSize = 22.sp) }

        Text("weight, all muted", style = MaterialTheme.typography.titleSmall)
        Probe("400") { Text(SAMPLE, color = Oal.Muted, fontWeight = FontWeight.Normal) }
        Probe("500") { Text(SAMPLE, color = Oal.Muted, fontWeight = FontWeight.Medium) }
        Probe("700") { Text(SAMPLE, color = Oal.Muted, fontWeight = FontWeight.Bold) }

        // Near-white on each surface. The volume numbers are hollow on a
        // card and solid on the ground, with no other difference.
        Text("background, all near-white", style = MaterialTheme.typography.titleSmall)
        Probe("ground") {
            Box(Modifier.background(Oal.Ground).padding(4.dp)) { Text(SAMPLE, color = Oal.Text) }
        }
        Probe("panel") {
            Box(Modifier.background(Oal.Panel).padding(4.dp)) { Text(SAMPLE, color = Oal.Text) }
        }
        Probe("panel2") {
            Box(Modifier.background(Oal.Panel2).padding(4.dp)) { Text(SAMPLE, color = Oal.Text) }
        }

        Text("widget", style = MaterialTheme.typography.titleSmall)
        Probe("plain Text") { Text("Hg 0123") }
        Probe("Button") { Button(onClick = {}) { Text("Hg 0123") } }
        Probe("Outlined") { OutlinedButton(onClick = {}) { Text("Hg 0123") } }
        Probe("TextButton") { TextButton(onClick = {}) { Text("Hg 0123") } }
    }
}
