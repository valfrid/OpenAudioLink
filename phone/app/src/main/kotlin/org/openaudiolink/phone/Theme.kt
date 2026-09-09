package org.openaudiolink.phone

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
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
 * Typography, and the one decision in it that is not cosmetic.
 *
 * `bodySmall` carries every counter in the app — packets, gaps,
 * milliseconds — and those are read by comparing one reading against
 * another. Proportional digits make each number a different width, so a
 * count that changes appears to jitter sideways.
 *
 * The first attempt fixed that with a **monospace family**, and it was the
 * wrong tool: it changed the typeface of whole sentences, not just their
 * digits, so the diagnostics panel read as a different application pasted
 * into this one. The alignment it bought only pays off in columns, and
 * these lines are prose that happens to contain numbers.
 *
 * `fontFeatureSettings = "tnum"` is the right one. It asks the *same*
 * typeface for its tabular figures, which is precisely what the Hub does
 * — `oal.css` sets `font-variant-numeric: tabular-nums` on its own
 * counters and changes nothing else about them.
 */
private val OalTypography = Typography().let { base ->
    base.copy(
        headlineSmall = base.headlineSmall.copy(fontWeight = FontWeight.SemiBold),
        titleLarge = base.titleLarge.copy(fontWeight = FontWeight.SemiBold, fontSize = 20.sp),
        titleMedium = base.titleMedium.copy(fontWeight = FontWeight.Medium),
        labelLarge = base.labelLarge.copy(fontWeight = FontWeight.Medium),
    )
}

/** The style every counter and log line uses. See the note above. */
val Diagnostic: TextStyle
    @Composable get() = MaterialTheme.typography.bodySmall.copy(
        fontFeatureSettings = "tnum",
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
