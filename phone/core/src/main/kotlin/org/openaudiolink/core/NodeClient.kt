package org.openaudiolink.core

import kotlinx.serialization.Serializable
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL

/**
 * The four controls a phone needs, and nothing else.
 *
 * Decision 19: the phone is a Producer with just enough Controller to get
 * a stream running. Every call here is an endpoint the Hub's web UI
 * already exercises, so none of this asks anything new of a node —
 * `protocol/CONTROL.md` is the contract.
 *
 * Deliberately absent, and to stay absent: OTA, the sample log, room
 * measurement. Those are the Hub's, and a speaker carries its own
 * correction in NVS, so it travels without the phone knowing it exists.
 */
class NodeClient(
    private val address: String,
    private val port: Int = Discovery.DEFAULT_CONTROL_PORT,
    private val timeoutMs: Int = 3_000,
) {
    /** What a node says about itself. A subset: this app reads little. */
    @Serializable
    data class Status(
        val id: String = "",
        val name: String = "",
        val roles: List<String> = emptyList(),
        val fw: String = "",
        val volume: Int = -1,
        val eqEnabled: Boolean = false,
    ) {
        /** Firmware older than 0.11.0 has no volume at all, and -1 says
         * "this node cannot" rather than "this node is silent". */
        val hasVolume: Boolean get() = volume >= 0
    }

    fun status(): Status? = get("/status")?.let {
        try {
            Discovery.json.decodeFromString(Status.serializer(), it)
        } catch (_: Exception) {
            null
        }
    }

    /** 0-100, attenuation only. Takes effect on the next 5 ms chunk. */
    fun setVolume(percent: Int): Boolean =
        post("/volume", Requests.volume(percent))

    /** Room correction on or off, on this node alone. */
    fun setRoomCorrection(enabled: Boolean): Boolean =
        post("/config", Requests.roomCorrection(enabled))

    /* ---------- the vinyl node: the phone is its Controller, not its source ---------- */

    fun startStream(destinations: List<String>, source: String = "capture"): Boolean =
        post("/stream/start", Requests.streamStart(destinations, source))

    fun stopStream(): Boolean = post("/stream/stop", "{}")

    /** Adding or removing a speaker mid-song, without interrupting it. */
    fun changeDestinations(add: List<String> = emptyList(),
                           remove: List<String> = emptyList()): Boolean =
        post("/stream/destinations", Requests.destinations(add, remove))

    private fun get(path: String): String? = request("GET", path, null)

    private fun post(path: String, body: String): Boolean =
        request("POST", path, body) != null

    private fun request(method: String, path: String, body: String?): String? {
        val connection = try {
            URL("http://$address:$port$path").openConnection() as HttpURLConnection
        } catch (_: IOException) {
            return null
        }
        return try {
            connection.requestMethod = method
            connection.connectTimeout = timeoutMs
            connection.readTimeout = timeoutMs
            if (body != null) {
                connection.doOutput = true
                connection.setRequestProperty("Content-Type", "application/json")
                connection.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
            }
            if (connection.responseCode !in 200..299) {
                null
            } else {
                connection.inputStream.use { it.readBytes().toString(Charsets.UTF_8) }
            }
        } catch (_: IOException) {
            // A speaker that has gone off the network is the ordinary case
            // at a party, not an exceptional one. The caller re-reads the
            // peer table and finds out soon enough.
            null
        } finally {
            connection.disconnect()
        }
    }
}

/**
 * The request bodies, built as strings and tested as strings.
 *
 * Separated from the transport so the wire format can be checked without a
 * node on the other end. A malformed body here is a 400 nobody sees on a
 * phone, which is exactly the kind of fault that gets blamed on the
 * network.
 */
object Requests {
    fun volume(percent: Int): String {
        require(percent in 0..100) { "volume is 0-100; amplifying digitally would clip" }
        return """{"percent":$percent}"""
    }

    fun roomCorrection(enabled: Boolean): String = """{"eqEnabled":$enabled}"""

    fun streamStart(destinations: List<String>, source: String): String {
        require(destinations.isNotEmpty()) { "a stream needs somewhere to go" }
        return """{"destinations":${quoted(destinations)},""" +
            """"port":${Rtp.DEFAULT_PORT},"source":"$source"}"""
    }

    /**
     * Both keys are optional and removals are applied first, so moving a
     * speaker between rooms is not refused for filling the set with an
     * entry that is on its way out.
     */
    fun destinations(add: List<String> = emptyList(),
                     remove: List<String> = emptyList()): String {
        val parts = buildList {
            if (remove.isNotEmpty()) add(""""remove":${quoted(remove)}""")
            if (add.isNotEmpty()) add(""""add":${quoted(add)}""")
        }
        return "{${parts.joinToString(",")}}"
    }

    private fun quoted(values: List<String>): String =
        values.joinToString(",", "[", "]") { "\"$it\"" }
}
