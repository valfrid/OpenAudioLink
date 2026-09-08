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

    /** The interface a multicast socket must join the group on. */
    fun multicastInterface(): NetworkInterface? {
        val name = linkProperties()?.interfaceName ?: return null
        return try {
            NetworkInterface.getByName(name)
        } catch (e: Exception) {
            Log.w(TAG, "no interface named $name", e)
            null
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

    private companion object {
        const val TAG = "oal.wifi"
    }
}
