package org.openaudiolink.phone.sources

import android.content.Context

/**
 * The build with Spotify in it.
 *
 * The same shape as the plain flavour's file, so the rest of the app is
 * identical in both builds and nothing has to test for a flavour.
 */
object SpotifySupport {
    const val AVAILABLE = true

    fun create(context: Context, name: String): AudioSource? = SpotifySource(context, name)
}
