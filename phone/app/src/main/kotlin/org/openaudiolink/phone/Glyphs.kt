package org.openaudiolink.phone

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.PathFillType
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.PathBuilder
import androidx.compose.ui.graphics.vector.path
import androidx.compose.ui.unit.dp

/**
 * Placeholder marks, drawn here rather than fetched.
 *
 * Four reasons they are hand-drawn shapes in a file instead of an icon
 * dependency or a set of PNGs:
 *
 *  - **They tint.** A vector drawn in black and rendered through `Icon`
 *    takes the theme's colour, so the same file is correct in light and
 *    dark without a second copy.
 *  - **They cost nothing.** No new artifact to resolve, which matters in
 *    a build that already fetches one binary over the network.
 *  - **They are honestly provisional.** These are geometry, not a brand.
 *    When there is a real visual language they get replaced wholesale, and
 *    nothing outside this file needs to know.
 *  - **No borrowed marks.** The Spotify tile deliberately carries a
 *    generic broadcast glyph rather than anything resembling Spotify's
 *    logo. That mark is a trademark and this project has no licence to
 *    draw it; the word "Spotify" beside the tile is a factual statement of
 *    what the feature talks to, which is a different thing entirely.
 *
 * The Hub's web UI uses emoji for the same job (📻 💿 🟢). Aligning the two
 * is part of the branding pass, not this one.
 */
object Glyphs {

    /** A speaker cabinet: two drivers punched out of a box. */
    val Speaker: ImageVector = glyph("oal.speaker") {
        path(fill = SolidColor(Color.Black), pathFillType = PathFillType.EvenOdd) {
            moveTo(5f, 2f)
            lineTo(19f, 2f)
            lineTo(19f, 22f)
            lineTo(5f, 22f)
            close()
            circle(12f, 15f, 3.4f)
            circle(12f, 6.5f, 1.5f)
        }
    }

    /** A record: groove, label, spindle hole. */
    val Vinyl: ImageVector = glyph("oal.vinyl") {
        path(fill = SolidColor(Color.Black), pathFillType = PathFillType.EvenOdd) {
            circle(12f, 12f, 10f)
            circle(12f, 12f, 4.2f)
        }
        path(fill = SolidColor(Color.Black), pathFillType = PathFillType.EvenOdd) {
            circle(12f, 12f, 2.6f)
            circle(12f, 12f, 0.9f)
        }
    }

    /**
     * Something being broadcast to the room — the Spotify tile's mark.
     *
     * Concentric arcs over a point. Deliberately generic: see the note
     * above about not drawing somebody else's trademark.
     */
    val Broadcast: ImageVector = glyph("oal.broadcast") {
        path(fill = SolidColor(Color.Black)) {
            circle(12f, 19f, 2f)
        }
        for (r in listOf(5f, 8.5f, 12f)) {
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
            ) {
                val dx = r * 0.7071f
                moveTo(12f - dx, 20f - dx)
                arcTo(r, r, 0f, false, true, 12f + dx, 20f - dx)
            }
        }
    }

    /** A beamed pair of notes: something stored on this phone. */
    val MusicFile: ImageVector = glyph("oal.musicfile") {
        path(fill = SolidColor(Color.Black)) {
            // The beam, sloping the way a written one does.
            moveTo(9f, 3.5f)
            lineTo(20f, 2f)
            lineTo(20f, 5f)
            lineTo(9f, 6.5f)
            close()
            // Two stems.
            moveTo(9f, 5f)
            lineTo(10.6f, 5f)
            lineTo(10.6f, 17f)
            lineTo(9f, 17f)
            close()
            moveTo(18.4f, 3.7f)
            lineTo(20f, 3.5f)
            lineTo(20f, 15.5f)
            lineTo(18.4f, 15.5f)
            close()
            // Two heads.
            circle(7.6f, 17.4f, 2.6f)
            circle(17f, 15.9f, 2.4f)
        }
    }

    /** A wave: the test tone, which is a wave and nothing else. */
    val Tone: ImageVector = glyph("oal.tone") {
        path(
            stroke = SolidColor(Color.Black),
            strokeLineWidth = 2f,
            strokeLineCap = StrokeCap.Round,
        ) {
            moveTo(2.5f, 12f)
            curveTo(4.5f, 4.5f, 6.5f, 4.5f, 8.5f, 12f)
            curveTo(10.5f, 19.5f, 12.5f, 19.5f, 14.5f, 12f)
            curveTo(16.2f, 6.2f, 18.5f, 6.2f, 21.5f, 12f)
        }
    }
}

private fun glyph(name: String, body: ImageVector.Builder.() -> Unit): ImageVector =
    ImageVector.Builder(
        name = name,
        defaultWidth = 24.dp,
        defaultHeight = 24.dp,
        viewportWidth = 24f,
        viewportHeight = 24f,
    ).apply(body).build()

/**
 * A circle, which the path DSL has no word for.
 *
 * Two half-arcs, because an arc command cannot describe a full turn: its
 * start and end points would coincide and the sweep would be undefined.
 */
private fun PathBuilder.circle(cx: Float, cy: Float, r: Float) {
    moveTo(cx - r, cy)
    arcToRelative(r, r, 0f, true, true, 2 * r, 0f)
    arcToRelative(r, r, 0f, true, true, -2 * r, 0f)
    close()
}
