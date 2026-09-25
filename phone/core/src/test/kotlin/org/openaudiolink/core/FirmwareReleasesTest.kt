package org.openaudiolink.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

/**
 * Choosing among releases, which is where the first version got it wrong.
 *
 * The shapes below are this repository's real ones, taken from the API
 * rather than imagined: a rolling `hub-latest` marked as a prerelease and
 * carrying the firmware, and several librespot releases that are ordinary
 * releases carrying none. Every rule in [Firmware.parseReleases] exists
 * because one of those two facts broke the obvious approach.
 */
class FirmwareReleasesTest {

    /** librespot first, as GitHub really returns it; firmware last. */
    private val asPublished = """
        [
          {"tag_name":"librespot-android-v0.8.0-2","prerelease":false,
           "created_at":"2026-09-08T09:03:18Z",
           "assets":[{"name":"liblibrespot.so",
                      "browser_download_url":"https://example.test/lib.so"}]},
          {"tag_name":"librespot-v0.8.0","prerelease":false,
           "created_at":"2026-08-26T08:56:22Z","assets":[]},
          {"tag_name":"hub-latest","prerelease":true,
           "created_at":"2026-08-07T17:02:52Z",
           "assets":[
             {"name":"OpenAudioLink-Hub-win-x64-0.106.0.zip",
              "browser_download_url":"https://example.test/hub.zip"},
             {"name":"testnode-esp32s3-0.56.0-flash.bin",
              "browser_download_url":"https://example.test/flash.bin"},
             {"name":"testnode-esp32s3-0.56.0-ota.bin",
              "browser_download_url":"https://example.test/ota.bin"},
             {"name":"testnode-esp32s3-0.56.0.sha256",
              "browser_download_url":"https://example.test/sums"}]}
        ]
    """.trimIndent()

    @Test
    fun `a prerelease carrying firmware is still the answer`() {
        // /releases/latest skips prereleases and returned the librespot
        // release, so the check reported "no image published" while the
        // image sat in a release the query declined to look at.
        val found = Firmware.parseReleases(asPublished)
        assertEquals("0.56.0", found?.version)
        assertEquals("https://example.test/ota.bin", found?.imageUrl)
        assertEquals("https://example.test/sums", found?.checksumUrl)
    }

    @Test
    fun `releases with no firmware are passed over rather than chosen`() {
        val found = Firmware.parseReleases(asPublished)
        assertEquals("testnode-esp32s3-0.56.0-ota.bin", found?.imageName)
    }

    @Test
    fun `the newest firmware wins, not the first or the newest release`() {
        /*
         * The date trap, which survives fixing the prerelease one.
         * `hub-latest` is republished in place, so its created_at stays at
         * whenever it was first made while its contents are from today —
         * an August release holding an image built this morning, listed
         * below September releases that hold none.
         */
        val twoBuilds = """
            [
              {"tag_name":"hub-v0.1.0","created_at":"2026-09-20T00:00:00Z",
               "assets":[{"name":"testnode-esp32s3-0.9.0-ota.bin",
                          "browser_download_url":"https://example.test/old.bin"}]},
              {"tag_name":"hub-latest","created_at":"2026-08-07T00:00:00Z",
               "assets":[{"name":"testnode-esp32s3-0.56.0-ota.bin",
                          "browser_download_url":"https://example.test/new.bin"}]}
            ]
        """.trimIndent()
        val found = Firmware.parseReleases(twoBuilds)
        // 0.56.0 over 0.9.0: later in version, earlier in date, second in
        // the list. Every ordering but the right one says the other file.
        assertEquals("0.56.0", found?.version)
        assertEquals("https://example.test/new.bin", found?.imageUrl)
    }

    @Test
    fun `no firmware anywhere is no answer`() {
        val none = """
            [{"tag_name":"librespot-v0.8.0","assets":[]},
             {"tag_name":"x","assets":[{"name":"readme.txt",
              "browser_download_url":"u"}]}]
        """.trimIndent()
        assertNull(Firmware.parseReleases(none))
    }

    @Test
    fun `an empty list, and nonsense, are both no answer`() {
        assertNull(Firmware.parseReleases("[]"))
        assertNull(Firmware.parseReleases("{}"))
        assertNull(Firmware.parseReleases("not json"))
        assertNull(Firmware.parseReleases(null))
    }
}
