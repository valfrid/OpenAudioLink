package org.openaudiolink.core

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/**
 * A firmware image CI has published, and enough to fetch it safely.
 *
 * @property version the version the image carries, read out of its file
 * name rather than out of the release tag. The tag is `hub-v<hub version>`
 * — a node and the Hub are on different clocks — so the tag says nothing
 * about the firmware inside.
 * @property checksumUrl the `.sha256` published beside it. Nullable
 * because a release without one is a release this app refuses rather than
 * one it trusts; see [FirmwareStore]'s rule, which is the Hub's own:
 * `get-librespot.ps1` will not install a binary that publishes no hash.
 */
data class FirmwareRelease(
    val version: String,
    val imageName: String,
    val imageUrl: String,
    val checksumUrl: String?,
)

/**
 * Reading what CI published, and deciding whether a node is behind.
 *
 * Here rather than in the app because it is a format and a comparison —
 * the two things most worth a host test and least worth a device. The
 * download, the verification and the serving are Android's problem; which
 * file to fetch and whether it is newer are not.
 */
object Firmware {

    /**
     * The OTA image among a release's assets.
     *
     * `release.yml` publishes three files per firmware build:
     *
     *     testnode-esp32s3-0.56.0-ota.bin     <- this one
     *     testnode-esp32s3-0.56.0-flash.bin
     *     testnode-esp32s3-0.56.0.sha256
     *
     * The `-flash.bin` is a merged image for a cable and is **not** what
     * OTA takes — `protocol/OTA.md` says application images only. Picking
     * the wrong one of two files whose names differ by one word is exactly
     * the mistake a regex should be made to prevent.
     */
    private val OTA_IMAGE = Regex("""^testnode-esp32s3-(.+)-ota\.bin$""")

    private val json = Json { ignoreUnknownKeys = true }

    @Serializable
    private data class Asset(
        val name: String,
        @SerialName("browser_download_url") val url: String,
    )

    @Serializable
    private data class Release(val assets: List<Asset> = emptyList())

    /**
     * @return the OTA image in a GitHub release, or null if it has none.
     *
     * Tolerant of everything else in that document, which is large and
     * changes: `ignoreUnknownKeys` means a new field from GitHub cannot
     * break an update check.
     */
    fun parseRelease(body: String?): FirmwareRelease? {
        if (body.isNullOrBlank()) return null
        val release = try {
            json.decodeFromString(Release.serializer(), body)
        } catch (_: Exception) {
            return null
        }

        val image = release.assets.firstNotNullOfOrNull { asset ->
            OTA_IMAGE.find(asset.name)?.let { match -> asset to match.groupValues[1] }
        } ?: return null

        val (asset, version) = image
        return FirmwareRelease(
            version = version,
            imageName = asset.name,
            imageUrl = asset.url,
            // Named for the version rather than for the image, because
            // one .sha256 covers both .bin files in a release.
            checksumUrl = release.assets
                .firstOrNull { it.name == "testnode-esp32s3-$version.sha256" }
                ?.url,
        )
    }

    /**
     * Pulls one file's hash out of `sha256sum` output.
     *
     * The published file covers every image in the release, so the name
     * has to be matched rather than the first line taken:
     *
     *     9f86d0…  testnode-esp32s3-0.56.0-ota.bin
     *     2c6242…  testnode-esp32s3-0.56.0-flash.bin
     *
     * Both spacings are accepted — `sha256sum` writes two spaces for text
     * mode and a space and a star for binary — because which one appears
     * depends on how the runner invoked it, and being wrong about that
     * would reject a perfectly good image.
     */
    fun sha256For(sums: String?, fileName: String): String? {
        if (sums.isNullOrBlank()) return null
        for (line in sums.lineSequence()) {
            val trimmed = line.trim()
            if (trimmed.isEmpty()) continue
            val hash = trimmed.substringBefore(' ').lowercase()
            if (hash.length != 64 || !hash.all { it in "0123456789abcdef" }) continue
            val named = trimmed.substringAfter(' ').trim().removePrefix("*").trim()
            if (named == fileName) return hash
        }
        return null
    }

    /**
     * Whether [candidate] is a later version than [installed].
     *
     * **Numeric, field by field, and that is the whole point.** A string
     * comparison puts 0.56.0 *before* 0.9.0, so a house full of nodes on
     * 0.9 would be told they were ahead of a release nearly fifty
     * versions newer — and an update check that reports "up to date" is
     * indistinguishable from one that is working.
     *
     * Unknown means no. A node that reports no version at all, or a
     * release whose name will not parse, produces false rather than an
     * offer to install something over something unidentified.
     */
    fun isNewer(candidate: String?, installed: String?): Boolean {
        val a = fields(candidate) ?: return false
        val b = fields(installed) ?: return false
        for (i in 0 until maxOf(a.size, b.size)) {
            val left = a.getOrElse(i) { 0 }
            val right = b.getOrElse(i) { 0 }
            if (left != right) return left > right
        }
        return false
    }

    /**
     * `0.56.0` to `[0, 56, 0]`, or null if it is not a version.
     *
     * A trailing suffix is dropped rather than refused — `0.56.0-rc1`
     * compares as 0.56.0 — because the alternative is an update check
     * that silently stops working the first time somebody tags a release
     * candidate.
     */
    private fun fields(version: String?): List<Int>? {
        val text = version?.trim()?.removePrefix("v") ?: return null
        if (text.isEmpty()) return null
        val parts = text.substringBefore('-').substringBefore('+').split('.')
        val numbers = parts.mapNotNull { it.toIntOrNull() }
        return numbers.takeIf { it.isNotEmpty() && it.size == parts.size }
    }
}
