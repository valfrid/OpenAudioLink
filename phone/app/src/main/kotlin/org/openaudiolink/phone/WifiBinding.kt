package org.openaudiolink.phone

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.wifi.WifiManager
import android.util.Log
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.NetworkInterface

/**
 * Keeping the audio on the Wi-Fi, and the radio awake to carry it.
 *
 * Three things that are invisible when they are missing, and that between
 * them account for most of the ways a phone producer fails without an
 * error anywhere:
 *
 *  - **A multicast lock.** The chip filters multicast frames before any
 *    socket sees them. Without the lock, discovery finds nothing at all.
 *  - **A high-performance Wi-Fi lock.** Station power save batches frames
 *    into beacon intervals. The consumer's servo absorbs that, but it
 *    turns a quiet link into a bursty one for no benefit.
 *  - **Explicit socket binding.** A phone with mobile data up is
 *    multi-homed, and a socket left to choose for itself will send a
 *    speaker's audio to the cellular interface, where it vanishes.
 *
 * The last of these is the Android form of the lesson decision 15 records
 * for the Hub. It is also the arrangement that makes a hotspot party work:
 * the speakers are served over Wi-Fi while the music arrives over mobile
 * data, and both are up at once.
 */
class WifiBinding(context: Context) {

    private val appContext = context.applicationContext
    private val connectivity =
        appContext.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
    private val wifi =
        appContext.getSystemService(Context.WIFI_SERVICE) as WifiManager

    private var multicastLock: WifiManager.MulticastLock? = null
    private var wifiLock: WifiManager.WifiLock? = null

    /**
     * The Wi-Fi network, or null if there is none.
     *
     * Null is a real answer and the caller has to say so out loud rather
     * than fall back to "whatever the system picks": that fallback is the
     * cellular interface, and audio sent there is simply gone.
     */
    fun wifiNetwork(): Network? = connectivity.allNetworks.firstOrNull { network ->
        connectivity.getNetworkCapabilities(network)
            ?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true
    }

    fun linkProperties(): LinkProperties? = wifiNetwork()?.let { connectivity.getLinkProperties(it) }

    /** This phone's own address on the Wi-Fi, for the announce it sends. */
    fun localAddress(): String? = linkProperties()
        ?.linkAddresses
        ?.map { it.address }
        ?.filterIsInstance<Inet4Address>()
        ?.firstOrNull()
        ?.hostAddress

    /**
     * The interface a multicast socket must join the group on.
     *
     * Three ways of asking, because one was not enough on a real phone.
     * The first build asked `LinkProperties` for a name and gave up when
     * that produced nothing `NetworkInterface` could resolve — and on a
     * handset at home it produced nothing, so discovery joined on the
     * system's choice and outbound announces started failing with
     * ENETUNREACH. Receiving still worked, which is what made it hard to
     * see: the Hub appeared in the list, so discovery looked fine, while
     * this phone was invisible to everything else on the network.
     *
     * ENETUNREACH is the shape of the underlying problem. A phone with
     * mobile data up is multi-homed, the default route is usually the
     * cellular one, and there is no route to 239.255.41.10 down it — so a
     * multicast send with no interface chosen has nowhere to go.
     */
    fun multicastInterface(): NetworkInterface? {
        val name = linkProperties()?.interfaceName
        if (name != null) {
            byName(name)?.let { return it }
            Log.w(TAG, "the Wi-Fi link calls itself $name, which is not a visible interface")
        }

        /*
         * Second: whichever interface actually holds this phone's Wi-Fi
         * address. The address comes from the same LinkProperties, so this
         * agrees with the first answer whenever both exist — it just does
         * not depend on the name resolving.
         */
        localAddress()?.let { address ->
            byAddress(address)?.let {
                Log.i(TAG, "using ${it.name}, found by address $address")
                return it
            }
        }

        /*
         * Third: the first interface that could carry a group at all.
         * A guess, and a much better one than the system default, which on
         * this hardware is the cellular interface.
         */
        return firstUsable()?.also {
            Log.w(TAG, "falling back to ${it.name} by inspection; the Wi-Fi named none")
        }
    }

    private fun byName(name: String): NetworkInterface? = try {
        NetworkInterface.getByName(name)?.takeIf { it.isUp && it.supportsMulticast() }
    } catch (e: Exception) {
        Log.w(TAG, "could not look up the interface named $name", e)
        null
    }

    private fun byAddress(address: String): NetworkInterface? = try {
        NetworkInterface.getNetworkInterfaces().toList().firstOrNull { candidate ->
            candidate.inetAddresses.toList().any { it.hostAddress == address }
        }?.takeIf { it.isUp && it.supportsMulticast() }
    } catch (e: Exception) {
        Log.w(TAG, "could not match an interface to $address", e)
        null
    }

    /**
     * Any interface that could carry a multicast group, Wi-Fi first.
     *
     * `wlan` before anything else by name, because the alternative on a
     * phone is `rmnet` — the cellular interface, and the one place a
     * speaker's audio is guaranteed not to be.
     */
    private fun firstUsable(): NetworkInterface? = try {
        NetworkInterface.getNetworkInterfaces().toList()
            .filter { candidate ->
                candidate.isUp &&
                    !candidate.isLoopback &&
                    candidate.supportsMulticast() &&
                    candidate.inetAddresses.toList().any { it is Inet4Address }
            }
            .minByOrNull { if (it.name.startsWith("wlan")) 0 else 1 }
    } catch (e: Exception) {
        Log.w(TAG, "could not enumerate interfaces", e)
        null
    }

    /**
     * Marks a socket's traffic as real-time audio, for the radio's sake.
     *
     * **This is the one lever left after the producer was measured and
     * cleared.** Over 38 minutes the phone's own pacing was late by more
     * than 15 ms just 33 times, the worst by 34 ms, and never once by more
     * than 50 — while the speakers were reporting 41 gaps over 200 ms
     * every thirty seconds. The packets left on time and arrived in
     * clumps, and the nodes are not asleep: the firmware sets
     * `WIFI_PS_NONE`. So the bunching happens in the air, or on the way to
     * it.
     *
     * DSCP 46 — expedited forwarding, `0xB8` once shifted into the byte —
     * is what Wi-Fi's WMM maps to the **voice** access category. Voice
     * contends for the medium with a much shorter window than best effort
     * and is not held back to be aggregated into a larger frame, which is
     * exactly the mechanism that turns an evenly paced stream into
     * quarter-second bursts. Unmarked, this audio has been competing as
     * ordinary background traffic all along.
     *
     * Best effort in the other sense too: an operating system may ignore
     * it, and a network without WMM certainly will. It costs one system
     * call and cannot make anything worse.
     */
    private fun expedite(socket: DatagramSocket) {
        try {
            socket.trafficClass = DSCP_EXPEDITED_FORWARDING
        } catch (e: Exception) {
            Log.w(TAG, "could not mark the audio socket as voice traffic", e)
        }
    }

    /**
     * A datagram socket pinned to the Wi-Fi.
     *
     * Returns null rather than an unbound socket when there is no Wi-Fi.
     * An unbound socket would work perfectly, send everything to the
     * cellular network, and report no error at all.
     */
    fun boundSocket(): DatagramSocket? {
        val network = wifiNetwork() ?: return null
        val socket = DatagramSocket()
        expedite(socket)
        return try {
            network.bindSocket(socket)
            /*
             * A send buffer big enough for a catch-up burst. The pacer
             * will emit up to a quarter of a second back to back after a
             * scheduling hiccup, and a burst that overruns the socket
             * buffer is dropped in the kernel, where it looks like network
             * loss from every angle a phone can see.
             */
            socket.sendBufferSize = 256 * 1024
            socket
        } catch (e: Exception) {
            Log.e(TAG, "could not bind a socket to the Wi-Fi", e)
            socket.close()
            null
        }
    }

    /**
     * Pins an already-open socket to the Wi-Fi.
     *
     * The same reasoning as [boundSocket], for a socket this class did not
     * create — the discovery socket, which has to be a MulticastSocket.
     * Failure is logged rather than thrown: an unbound socket usually still
     * works, and losing discovery entirely would be the worse outcome.
     */
    fun bindToWifi(socket: java.net.DatagramSocket) {
        val network = wifiNetwork()
        if (network == null) {
            Log.w(TAG, "no Wi-Fi to bind the socket to")
            return
        }
        try {
            network.bindSocket(socket)
        } catch (e: Exception) {
            Log.w(TAG, "could not bind the socket to the Wi-Fi", e)
        }
    }

    fun acquireLocks() {
        if (multicastLock == null) {
            multicastLock = wifi.createMulticastLock("oal-discovery").apply {
                setReferenceCounted(false)
                acquire()
            }
        }
        if (wifiLock == null) {
            wifiLock = wifi.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "oal-audio")
                .apply {
                    setReferenceCounted(false)
                    acquire()
                }
        }
    }

    fun releaseLocks() {
        multicastLock?.let { if (it.isHeld) it.release() }
        wifiLock?.let { if (it.isHeld) it.release() }
        multicastLock = null
        wifiLock = null
    }

    /**
     * The same marking for a socket this class did not open.
     *
     * A phone hosting the hotspot has no Wi-Fi `Network` to bind to, so
     * the producer falls back to a plain socket — and that is the party
     * arrangement of decision 20, the one deployment this app exists for.
     * It deserves the priority marking as much as any other.
     */
    fun expediteAudio(socket: DatagramSocket) = expedite(socket)

    private companion object {
        const val TAG = "oal.wifi"

        /**
         * DSCP 46 in the traffic-class byte: the six-bit code point sits
         * in the top six bits, so 46 becomes 46 shl 2 = 184.
         */
        const val DSCP_EXPEDITED_FORWARDING = 0xB8
    }
}
