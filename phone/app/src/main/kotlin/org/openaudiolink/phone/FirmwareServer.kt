package org.openaudiolink.phone

import android.util.Log
import java.io.File
import java.io.OutputStream
import java.net.ServerSocket
import java.net.Socket

/**
 * Serves one file over plain HTTP, for as long as an update is running.
 *
 * **This exists because of a limit in the node, not a preference here.**
 * `ota_task()` calls `esp_https_ota()` with no certificate bundle compiled
 * in — `protocol/OTA.md` spells it out — so there are no roots to verify a
 * server against and in practice only `http://` works. GitHub is HTTPS
 * only. Something on the local network has to stand between the two, and
 * on a wall panel that is this.
 *
 * It is deliberately not a web server. One file, one content type, no
 * routing, no directory, no upload. Whatever path is asked for gets the
 * image, because the only client is a node that was handed the URL a
 * moment earlier by this same app.
 *
 * **It runs only while an update is in flight.** This is the one place the
 * app accepts inbound connections rather than making them, and a socket
 * that is open all evening on a device mounted in a hallway is a larger
 * promise than a firmware update needs. [stop] is called when the last
 * node has been told, or when the attempt gives up.
 */
class FirmwareServer(private val image: File) {

    private var socket: ServerSocket? = null
    private var thread: Thread? = null
    @Volatile private var running = false

    /** @return the port it is listening on, or null if it could not start. */
    fun start(): Int? {
        if (running) return socket?.localPort

        val open = try {
            // Port 0: let the system choose. A fixed port would collide
            // with whatever else is on a tablet somebody also uses.
            ServerSocket(0)
        } catch (e: Exception) {
            Log.e(TAG, "could not open a port to serve the image", e)
            return null
        }

        socket = open
        running = true
        thread = Thread({ serve(open) }, "oal-firmware").apply {
            isDaemon = true
            start()
        }
        Log.i(TAG, "serving ${image.name} (${image.length()} bytes) on port ${open.localPort}")
        return open.localPort
    }

    fun stop() {
        running = false
        try {
            socket?.close()
        } catch (_: Exception) {
        }
        socket = null
        thread = null
    }

    /**
     * How many nodes have actually fetched it.
     *
     * The node answers `accepted` to the POST and then works on its own,
     * so the request that matters — whether it came and took the bytes —
     * happens here and nowhere else. Without this, a node that never
     * reached the tablet looks exactly like one still downloading.
     */
    @Volatile var served: Int = 0
        private set

    private fun serve(open: ServerSocket) {
        while (running) {
            val client = try {
                open.accept()
            } catch (_: Exception) {
                return   // closed by stop(), which is how this ends
            }
            // One at a time. Five speakers updating together is five
            // sequential reads of a file already in the page cache, and a
            // thread pool here would be machinery for no gain.
            try {
                client.use { respond(it) }
            } catch (e: Exception) {
                Log.w(TAG, "a node dropped mid-download", e)
            }
        }
    }

    private fun respond(client: Socket) {
        val input = client.getInputStream().bufferedReader()

        /*
         * The request is read and discarded, but it must be *read*.
         * Replying to a request still sitting in the socket buffer and
         * then closing gets the peer a connection reset instead of the
         * body, which on the node's side reads as a network fault.
         */
        val requestLine = input.readLine() ?: return
        while (true) {
            val header = input.readLine() ?: break
            if (header.isEmpty()) break
        }
        Log.i(TAG, "${client.inetAddress.hostAddress} asked for: $requestLine")

        val out: OutputStream = client.getOutputStream()
        val length = image.length()
        val head = buildString {
            append("HTTP/1.1 200 OK\r\n")
            append("Content-Type: application/octet-stream\r\n")
            append("Content-Length: $length\r\n")
            // Closed rather than kept alive: the node asks once, and a
            // connection this end holds open is one more thing to time out.
            append("Connection: close\r\n\r\n")
        }
        out.write(head.toByteArray(Charsets.US_ASCII))
        image.inputStream().use { it.copyTo(out, DEFAULT_BUFFER_SIZE) }
        out.flush()
        served++
    }

    private companion object {
        const val TAG = "oal.firmware"
    }
}
