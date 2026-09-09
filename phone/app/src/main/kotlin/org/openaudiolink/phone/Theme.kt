package org.openaudiolink.phone

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.foundation.shape.RoundedCornerShape

/**
 * The Hub's palette, in Compose.
 *
 * Taken from `hub/src/OpenAudioLink.Hub/wwwroot/oal.css` rather than
 * chosen again here, because the two halves are one product and a person
 * who sets up a speaker in a browser and then picks it on a phone should
 * not have to be told they are the same system. The custom properties
 * there are the source of truth; these are the same values.
 *
 * **Dark, and not conditionally.** The Hub's web UI has no light mode and
 * neither does the launcher icon, so an app that follows the system theme
 * matches the Hub only on handsets that happen to be set to dark. The
 * first attempt did exactly that, and on a phone set to light it produced
 * the accent colour and none of the rest — a visual change that looked
 * like no visual change, which is a fair description of getting half a
 * design.
 *
 * So the choice is made here rather than deferred to a setting somebody
 * else made for other reasons. This is one product with one look, the
 * screen is most often read in a dim room with music playing, and matching
 * the Hub was the whole instruction.
 *
 * The rounding is `oal.css`'s too: 18dp on cards, 12dp on controls. Small
 * thing, and it is most of why two interfaces look related at a glance.
 */
object Oal {
    val Ground = Color(0xFF111317)      // --bg
    val Panel = Color(0xFF1B1F25)       // --panel
    val Panel2 = Color(0xFF242A32)      // --panel2
    val Text = Color(0xFFF4F6F8)        // --text
    val Muted = Color(0xFF9EA8B3)       // --muted
    val Accent = Color(0xFF62D1A6)      // --accent
    val Border = Color(0xFF343B45)      // --border
    val Danger = Color(0xFFFF6B6B)      // --danger
    val Warn = Color(0xFFF0B866)        // --warn
}

private val DarkScheme = darkColorScheme(
    primary = Oal.Accent,
    // Text on the accent is the ground, not white: the accent is a light
    // mint and white on it is unreadable at any size worth pressing.
    onPrimary = Oal.Ground,
    primaryContainer = Oal.Panel2,
    onPrimaryContainer = Oal.Text,
    secondary = Oal.Accent,
    onSecondary = Oal.Ground,
    secondaryContainer = Oal.Panel2,
    onSecondaryContainer = Oal.Text,
    background = Oal.Ground,
    onBackground = Oal.Text,
    surface = Oal.Panel,
    onSurface = Oal.Text,
    surfaceVariant = Oal.Panel2,
    onSurfaceVariant = Oal.Muted,
    outline = Oal.Border,
    outlineVariant = Oal.Border,
    error = Oal.Danger,
    onError = Oal.Ground,
)

private val OalShapes = Shapes(
    extraSmall = RoundedCornerShape(8.dp),
    small = RoundedCornerShape(10.dp),
    medium = RoundedCornerShape(12.dp),
    large = RoundedCornerShape(18.dp),
    extraLarge = RoundedCornerShape(22.dp),
)

/**
 * The typeface, which until now the app never actually chose.
 *
 * Leaving `fontFamily` unset means `FontFamily.Default`, which on Android
 * is the *system* font — and on a Samsung handset that is whatever the
 * owner picked under Display → Font size and style. So this app rendered
 * in a face neither the Hub nor this project had any say in, and the
 * claim that it matched the Hub covered the palette and the rounding but
 * never the letters.
 *
 * It also showed. On a Galaxy A8 on Android 9 the screen came back with
 * two kinds of text on it: some lines drawn normally and others as
 * hollow, double-walled glyphs. Two attempts to explain that from the
 * app's own styles both failed — a monospace family in 0.8.2, then
 * `fontFeatureSettings = "tnum"` in 0.8.3, removed again in 0.8.6 when
 * the effect outlived it. Mapping every line on the screen to its style
 * settled that it was neither: `bodySmall` appeared on both sides of the
 * split, and so did `labelLarge`, which rules out size and weight as the
 * cause. What was left was the one thing the app had delegated.
 *
 * So it delegates it no longer. Roboto is bundled in `res/font` and every
 * style is pinned to it.
 *
 * **Roboto specifically**, for reasons rather than taste. `oal.css` asks
 * for `Inter, system-ui, -apple-system, "Segoe UI", Roboto, Arial,
 * sans-serif` and ships no `@font-face`, so the Hub renders in Inter only
 * where Inter happens to be installed — on Windows it is Segoe UI.
 * Roboto is the entry in the Hub's own stack that an Android device
 * would reach, it is Android's native UI face so the app still looks
 * like an Android app, and it is Apache-2.0, which a public repository
 * needs. The licence is in `phone/app/licenses/`.
 *
 * **Three real weights, and no fourth.** 400, 500 and 700 exist as files;
 * a request for anything else is answered by the platform picking the
 * nearest and *synthesising* the difference, which is a stroke applied to
 * a glyph — the same class of artefact as the one this is fixing. So the
 * two styles that asked for SemiBold (600) now ask for Bold, which is
 * also closer to the Hub, whose headings are 700-900.
 *
 * One thing given up: the counters have no tabular figures, so a changing
 * count shifts sideways by a pixel. Roboto's `tnum` could be asked for
 * now that the file is known and present — but that request is what
 * 0.8.3 did to the system font, and it is not worth re-opening until
 * this screen is confirmed clean.
 */
internal val OalRoboto = FontFamily(
    Font(R.font.roboto_regular, FontWeight.Normal),
    Font(R.font.roboto_medium, FontWeight.Medium),
    Font(R.font.roboto_bold, FontWeight.Bold),
)

private fun TextStyle.pin(weight: FontWeight): TextStyle =
    copy(fontFamily = OalRoboto, fontWeight = weight)

private val OalTypography = Typography().let { base ->
    base.copy(
        displayLarge = base.displayLarge.pin(FontWeight.Normal),
        displayMedium = base.displayMedium.pin(FontWeight.Normal),
        displaySmall = base.displaySmall.pin(FontWeight.Normal),
        headlineLarge = base.headlineLarge.pin(FontWeight.Normal),
        headlineMedium = base.headlineMedium.pin(FontWeight.Normal),
        headlineSmall = base.headlineSmall.pin(FontWeight.Bold),
        titleLarge = base.titleLarge.pin(FontWeight.Bold).copy(fontSize = 20.sp),
        titleMedium = base.titleMedium.pin(FontWeight.Medium),
        titleSmall = base.titleSmall.pin(FontWeight.Medium),
        bodyLarge = base.bodyLarge.pin(FontWeight.Normal),
        bodyMedium = base.bodyMedium.pin(FontWeight.Normal),
        bodySmall = base.bodySmall.pin(FontWeight.Normal),
        labelLarge = base.labelLarge.pin(FontWeight.Medium),
        labelMedium = base.labelMedium.pin(FontWeight.Medium),
        labelSmall = base.labelSmall.pin(FontWeight.Medium),
    )
}

/**
 * The style every counter and log line uses: `bodySmall`, set back a
 * shade so the instrumentation reads as instrumentation.
 *
 * Colour only, and deliberately so — it inherits the family and weight
 * from the theme like everything else. Two attempts to make this style
 * typographically special are described above; both had to come out.
 */
val Diagnostic: TextStyle
    @Composable get() = MaterialTheme.typography.bodySmall.copy(
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )

@Composable
fun OalTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = DarkScheme,
        typography = OalTypography,
        shapes = OalShapes,
        content = content,
    )
}
