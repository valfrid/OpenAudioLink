package org.openaudiolink.phone

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
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
 * **Dark first.** The web UI has no light mode and neither did the
 * launcher icon, so the phone matching it is the whole point. A light
 * scheme still exists below because Android will hand this app to somebody
 * with the system set to light and a screen that ignores the setting reads
 * as broken rather than opinionated — but it is the Hub's colours
 * rearranged, not a different design.
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

/**
 * The same palette for a light system, rearranged rather than replaced.
 *
 * The accent darkens, because #62d1a6 on white is decorative rather than
 * legible; everything else follows from that one substitution.
 */
private val LightScheme = lightColorScheme(
    primary = Color(0xFF12795A),
    onPrimary = Color.White,
    primaryContainer = Color(0xFFD3F1E5),
    onPrimaryContainer = Color(0xFF07281E),
    secondary = Color(0xFF12795A),
    onSecondary = Color.White,
    secondaryContainer = Color(0xFFD3F1E5),
    onSecondaryContainer = Color(0xFF07281E),
    background = Color(0xFFF7F9FA),
    onBackground = Color(0xFF111317),
    surface = Color.White,
    onSurface = Color(0xFF111317),
    surfaceVariant = Color(0xFFEDF1F3),
    onSurfaceVariant = Color(0xFF5A646E),
    outline = Color(0xFFC7CFD6),
    error = Color(0xFFB3261E),
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
 * count that changes appears to jitter sideways and two lines of figures
 * do not line up. There is no tabular-figures switch in Compose's default
 * font stack, so the next best thing is a monospace family for exactly
 * those lines, which is what `Diagnostic` below is for.
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
        fontFamily = androidx.compose.ui.text.font.FontFamily.Monospace,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )

@Composable
fun OalTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = if (isSystemInDarkTheme()) DarkScheme else LightScheme,
        typography = OalTypography,
        shapes = OalShapes,
        content = content,
    )
}
