package org.openaudiolink.phone

import android.content.Context
import android.util.Log
import org.openaudiolink.core.Firmware
import org.openaudiolink.core.FirmwareRelease
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

/**
 * Fetches a published firmware image and refuses to keep a wrong one.
 *
 * The tablet's half of the two-step: it has internet and can speak TLS, so
 * it fetches from where CI published the file and verifies it, and only
 * then does [FirmwareServer] offer it to nodes that cannot do either.
 *
 * **A release that publishes no checksum is refused, not trusted.** That
 * is `hub/scripts/get-librespot.ps1`'s rule and the Gradle librespot
 * fetch's rule, applied to the one download in this project that ends with
 * a device overwriting its own flash. The verification is the reason the
 * two-step is worth its extra moving part: a node fetching straight from
 * GitHub would install whatever arrived, because it has nothing to check
 * it against.
 */
object FirmwareStore {

    private const val TAG = "oal.firmware"

    /**
     * Where CI publishes. The rolling `hub-latest` release is republished
     * on every build of the default branch, so "latest" is genuinely the
     * newest image rather than the newest tagged one.
     */
    private const val LATEST =
        "https://api.github.com/repos/valfrid/OpenAudioLink/releases/latest"

    /** What a check found, and whether it can be installed. */
    data class Available(
        val release: FirmwareRelease,
        val sha256: String?,
    ) {
        /** No hash published, no install. See the note on this object. */
        val installable: Boolean get() = sha256 != null
    }

    /** The verified image on disk, if one has been downloaded. */
    fun cached(context: Context, release: FirmwareRelease): File? =
        File(imageDir(context), release.imageName).takeIf { it.isFile }

    private fun imageDir(context: Context): File =
        File(context.filesDir, "firmware").apply { mkdirs() }

    /**
     * Asks GitHub what the newest image is. Network call; not for the main
     * thread.
     */
    fun check(): Available? {
        val release = Firmware.parseRelease(fetchText(LATEST)) ?: return null
        val sums = release.checksumUrl?.let { fetchText(it) }
        return Available(release, Firmware.sha256For(sums, release.imageName))
    }

    /**
     * Downloads and verifies, returning the file only if the hash matches.
     *
     * Hashed while it streams rather than afterwards, so a wrong image is
     * never written under the name a node would be handed. It lands in a
     * temporary file and is renamed only once the digest agrees; a
     * download interrupted halfway leaves nothing that looks finished.
     */
    fun download(context: Context, available: Available): File? {
        val expected = available.sha256 ?: run {
            Log.e(TAG, "${available.release.imageName} publishes no SHA256; refusing it")
            return null
        }

        cached(context, available.release)?.let { existing ->
            if (digestOf(existing).equals(expected, ignoreCase = true)) {
                Log.i(TAG, "already have a verified ${existing.name}")
                return existing
            }
            existing.delete()
        }

        val target = File(imageDir(context), available.release.imageName)
        val partial = File(target.parentFile, "${target.name}.part")
        val digest = MessageDigest.getInstance("SHA-256")

        try {
            open(available.release.imageUrl).use { input ->
                partial.outputStream().use { output ->
                    val buffer = ByteArray(64 * 1024)
                    while (true) {
                        val read = input.read(buffer)
                        if (read <= 0) break
                        digest.update(buffer, 0, read)
                        output.write(buffer, 0, read)
                    }
                }
            }
        } catch (e: Exception) {
            Log.e(TAG, "downloading ${available.release.imageUrl} failed", e)
            partial.delete()
            return null
        }

        val actual = digest.digest().joinToString("") { "%02x".format(it) }
        if (!actual.equals(expected, ignoreCase = true)) {
            Log.e(TAG, "SHA256 mismatch: expected $expected, got $actual")
            partial.delete()
            return null
        }

        partial.renameTo(target)
        Log.i(TAG, "${target.name} verified: ${target.length()} bytes")
        return target
    }

    private fun digestOf(file: File): String {
        val digest = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { input ->
            val buffer = ByteArray(64 * 1024)
            while (true) {
                val read = input.read(buffer)
                if (read <= 0) break
                digest.update(buffer, 0, read)
            }
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }

    private fun fetchText(url: String): String? = try {
        open(url).use { it.readBytes().toString(Charsets.UTF_8) }
    } catch (e: Exception) {
        Log.w(TAG, "could not fetch $url", e)
        null
    }

    /**
     * Follows redirects by hand, because the interesting one is cross-host.
     *
     * A GitHub release asset answers with a 302 to
     * `objects.githubusercontent.com`, and `HttpURLConnection` refuses to
     * follow a redirect that changes host on its own — it returns the 302
     * and an empty body, which reads as a corrupt download rather than as
     * a redirect nobody followed. `protocol/OTA.md` names this same hop as
     * one of the two things to test before trusting a node to fetch from
     * GitHub directly; here it is simply handled.
     */
    private fun open(url: String): java.io.InputStream {
        var current = url
        repeat(5) {
            val connection = (URL(current).openConnection() as HttpURLConnection).apply {
                instanceFollowRedirects = false
                connectTimeout = 15_000
                readTimeout = 30_000
                setRequestProperty("Accept", "application/octet-stream, application/json")
                // GitHub's API answers 403 to a request with no User-Agent.
                setRequestProperty("User-Agent", "OpenAudioLink/${BuildInfo.VERSION}")
            }
            when (val code = connection.responseCode) {
                in 200..299 -> return connection.inputStream
                301, 302, 303, 307, 308 -> {
                    val next = connection.getHeaderField("Location")
                    connection.disconnect()
                    current = next ?: throw java.io.IOException("redirect with no Location")
                }
                else -> {
                    connection.disconnect()
                    throw java.io.IOException("HTTP $code from $current")
                }
            }
        }
        throw java.io.IOException("too many redirects from $url")
    }
}
