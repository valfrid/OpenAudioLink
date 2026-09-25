package org.openaudiolink.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Reading what CI published, and deciding who is behind.
 *
 * The asset names below are `release.yml`'s own, and the release document
 * is shaped like GitHub's rather than invented, because both are formats
 * this code does not control and a parser written against a guess works
 * until the day it matters.
 */
class FirmwareTest {

    private val release = """
        {
          "tag_name": "hub-v0.106.0",
          "name": "Hub 0.106.0",
          "assets": [
            {
              "name": "OpenAudioLink-Hub-win-x64-0.106.0.zip",
              "browser_download_url": "https://example.test/hub.zip"
            },
            {
              "name": "testnode-esp32s3-0.56.0-flash.bin",
              "browser_download_url": "https://example.test/flash.bin"
            },
            {
              "name": "testnode-esp32s3-0.56.0-ota.bin",
              "browser_download_url": "https://example.test/ota.bin"
            },
            {
              "name": "testnode-esp32s3-0.56.0.sha256",
              "browser_download_url": "https://example.test/sums"
            }
          ]
        }
    """.trimIndent()

    @Test
    fun `the OTA image is found among the other assets`() {
        val found = Firmware.parseRelease(release)
        assertEquals("0.56.0", found?.version)
        assertEquals("testnode-esp32s3-0.56.0-ota.bin", found?.imageName)
        assertEquals("https://example.test/ota.bin", found?.imageUrl)
        assertEquals("https://example.test/sums", found?.checksumUrl)
    }

    @Test
    fun `the flash image is never mistaken for the OTA one`() {
        // protocol/OTA.md: application images only. The two names differ
        // by one word and installing the merged one would be a bad day.
        val found = Firmware.parseRelease(release)
        assertFalse(found?.imageUrl?.contains("flash") == true)
    }

    @Test
    fun `the version comes from the file name and not the tag`() {
        // The release is tagged hub-v0.106.0 and the firmware inside is
        // 0.56.0. A node and the Hub are on different clocks.
        assertEquals("0.56.0", Firmware.parseRelease(release)?.version)
    }

    @Test
    fun `a release with no firmware in it is not one`() {
        val hubOnly = """{"assets":[{"name":"Hub.zip","browser_download_url":"u"}]}"""
        assertNull(Firmware.parseRelease(hubOnly))
    }

    @Test
    fun `a release with no checksum still parses, without one`() {
        // Whether to refuse it is the caller's rule, not the parser's.
        val bare = """
            {"assets":[{"name":"testnode-esp32s3-1.2.3-ota.bin",
             "browser_download_url":"u"}]}
        """.trimIndent()
        val found = Firmware.parseRelease(bare)
        assertEquals("1.2.3", found?.version)
        assertNull(found?.checksumUrl)
    }

    @Test
    fun `nonsense is not a release`() {
        assertNull(Firmware.parseRelease("not json"))
        assertNull(Firmware.parseRelease(""))
        assertNull(Firmware.parseRelease(null))
    }

    @Test
    fun `unknown fields do not break the parse`() {
        // GitHub's document is large and grows; a new field must not stop
        // an update check working.
        val extra = """
            {"url":"…","id":1,"author":{"login":"x"},"draft":false,
             "assets":[{"name":"testnode-esp32s3-2.0.0-ota.bin",
             "browser_download_url":"u","download_count":7,"uploader":{"id":2}}]}
        """.trimIndent()
        assertEquals("2.0.0", Firmware.parseRelease(extra)?.version)
    }

    /* ------------------------------------------------------- checksums */

    private val sums = """
        9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08  testnode-esp32s3-0.56.0-ota.bin
        2c624232cdd221771294dfbb310aca000a0df6ac8b66b696d90ef06fdefb64a3  testnode-esp32s3-0.56.0-flash.bin
    """.trimIndent()

    @Test
    fun `the hash is matched by name, not by position`() {
        assertEquals(
            "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
            Firmware.sha256For(sums, "testnode-esp32s3-0.56.0-ota.bin"),
        )
        assertEquals(
            "2c624232cdd221771294dfbb310aca000a0df6ac8b66b696d90ef06fdefb64a3",
            Firmware.sha256For(sums, "testnode-esp32s3-0.56.0-flash.bin"),
        )
    }

    @Test
    fun `binary-mode output is read too`() {
        // sha256sum writes "hash *name" for binary mode and "hash  name"
        // for text. Which appears depends on how the runner invoked it.
        val binary =
            "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08 *image.bin"
        assertEquals(
            "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
            Firmware.sha256For(binary, "image.bin"),
        )
    }

    @Test
    fun `a file that is not listed has no hash`() {
        assertNull(Firmware.sha256For(sums, "something-else.bin"))
        assertNull(Firmware.sha256For(null, "x.bin"))
        assertNull(Firmware.sha256For("garbage", "x.bin"))
    }

    /* -------------------------------------------------------- versions */

    @Test
    fun `0 56 0 is newer than 0 9 0`() {
        // The test this file exists for. Compared as strings, "0.56.0"
        // sorts before "0.9.0", so a house on 0.9 would be told it was
        // ahead of a release forty-seven versions newer — and an update
        // check that says "up to date" looks the same either way.
        assertTrue(Firmware.isNewer("0.56.0", "0.9.0"))
        assertFalse(Firmware.isNewer("0.9.0", "0.56.0"))
    }

    @Test
    fun `the same version is not newer`() {
        assertFalse(Firmware.isNewer("0.56.0", "0.56.0"))
    }

    @Test
    fun `a later patch counts`() {
        assertTrue(Firmware.isNewer("0.56.1", "0.56.0"))
        assertFalse(Firmware.isNewer("0.56.0", "0.56.1"))
    }

    @Test
    fun `a missing field is a zero`() {
        assertFalse(Firmware.isNewer("0.56", "0.56.0"))
        assertTrue(Firmware.isNewer("0.56.1", "0.56"))
    }

    @Test
    fun `a leading v is tolerated on either side`() {
        assertTrue(Firmware.isNewer("v0.57.0", "0.56.0"))
        assertTrue(Firmware.isNewer("0.57.0", "v0.56.0"))
    }

    @Test
    fun `a release candidate compares as its release`() {
        assertTrue(Firmware.isNewer("0.57.0-rc1", "0.56.0"))
        assertFalse(Firmware.isNewer("0.56.0-rc1", "0.56.0"))
    }

    @Test
    fun `unknown is never newer`() {
        // A node that reports nothing must not be offered an install of
        // something over something unidentified.
        assertFalse(Firmware.isNewer("0.56.0", null))
        assertFalse(Firmware.isNewer("0.56.0", ""))
        assertFalse(Firmware.isNewer(null, "0.56.0"))
        assertFalse(Firmware.isNewer("not-a-version", "0.56.0"))
        assertFalse(Firmware.isNewer("0.56.0", "unknown"))
    }
}
