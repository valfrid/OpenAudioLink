package org.openaudiolink.phone.sources

import android.content.Context

/**
 * The build without Spotify in it.
 *
 * Two files with one shape, one per flavour, so the rest of the app never
 * asks which build it is — it asks whether the source exists. That is the
 * containment decision 19 describes, made structural: this flavour has no
 * librespot binary and no code that could use one, and it is still a radio,
 * a library player and a tone generator.
 */
object SpotifySupport {
    const val AVAILABLE = false

    fun create(context: Context, name: String): AudioSource? = null
}
