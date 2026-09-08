package org.openaudiolink.phone

import android.content.Context
import android.provider.Settings
import org.openaudiolink.core.Station

/**
 * The handful of things this app remembers between launches.
 *
 * SharedPreferences and nothing else: two values, both trivial, and a
 * database would be more machinery than the whole app has elsewhere.
 *
 * Note what is *not* here. The Spotify credential lives where librespot
 * put it, in app-private storage, and is never copied into a preferences
 * file — it is reusable playback access to a real account, the same class
 * of secret this project keeps its Wi-Fi credentials out of the repository
 * for, and one copy of it is enough.
 */
object Prefs {

    private const val FILE = "openaudiolink"
    private const val KEY_CAST_NAME = "castName"
    private const val KEY_DETAILS = "showDetails"
    private const val KEY_SELECTED = "selectedSpeakers"
    private const val KEY_STATIONS = "stations"

    /**
     * What a cast point is called before anybody renames it.
     *
     * The prefix is the point. A Spotify device list is a flat alphabetical
     * pile of everything in the house, and a household running a Hub with
     * several rooms plus a phone wants those to arrive as a block rather
     * than scattered between a television and somebody's laptop. It is a
     * default, not a rule: the field below accepts anything.
     */
    const val PREFIX = "OAL "

    private fun prefs(context: Context) =
        context.getSharedPreferences(FILE, Context.MODE_PRIVATE)

    /** The phone's own name, which is the one a guest recognises. */
    private fun phoneName(context: Context): String =
        Settings.Global.getString(context.contentResolver, Settings.Global.DEVICE_NAME)
            ?: android.os.Build.MODEL
            ?: "phone"

    fun defaultCastName(context: Context): String = PREFIX + phoneName(context)

    fun castName(context: Context): String =
        prefs(context).getString(KEY_CAST_NAME, null)?.takeIf { it.isNotBlank() }
            ?: defaultCastName(context)

    /**
     * Renames the cast point.
     *
     * Blank means "go back to the default" rather than an empty name: a
     * nameless device in a Spotify picker is unpickable, and refusing the
     * edit would strand somebody who cleared the field to start again.
     */
    fun setCastName(context: Context, name: String) {
        val trimmed = name.trim()
        prefs(context).edit().apply {
            if (trimmed.isEmpty()) remove(KEY_CAST_NAME) else putString(KEY_CAST_NAME, trimmed)
        }.apply()
    }

    /**
     * Whether the counters, the log and the heartbeat are on screen.
     *
     * Off by default. They were written to answer questions a person
     * hitting a wall needs answered, and they earned their place doing
     * exactly that — but a screen that opens on packet counts and
     * librespot's stderr is an instrument panel, and this is meant to be
     * something somebody plays music with.
     */
    fun showDetails(context: Context): Boolean =
        prefs(context).getBoolean(KEY_DETAILS, false)

    fun setShowDetails(context: Context, show: Boolean) {
        prefs(context).edit().putBoolean(KEY_DETAILS, show).apply()
    }

    /**
     * Which speakers were ticked, so a restart does not silence a party.
     *
     * A `StringSet` would be the obvious type and is the wrong one: it
     * does not preserve order, and the order speakers were chosen in is
     * the order they appear. A joined string keeps it, and a device id has
     * no newline in it.
     *
     * This is preference, not state — if a remembered id belongs to a
     * device that never comes back it costs one entry that is never
     * matched, which is why nothing here has to expire.
     */
    fun selected(context: Context): List<String> =
        prefs(context).getString(KEY_SELECTED, null)
            ?.split("\n")
            ?.filter { it.isNotBlank() }
            ?: emptyList()

    fun setSelected(context: Context, ids: List<String>) {
        prefs(context).edit().putString(KEY_SELECTED, ids.joinToString("\n")).apply()
    }

    /**
     * Saved radio stations.
     *
     * JSON rather than a delimiter this time, because a station has two
     * fields somebody typed and one of them is a URL — and a URL can
     * contain very nearly anything. `kotlinx.serialization` is already a
     * dependency for the discovery protocol, so this costs nothing new.
     *
     * On the phone rather than on the Hub, unlike the Hub's own list, and
     * the difference is deliberate: this app exists to work at a party
     * where there may be no Hub at all. The two lists are the same shape,
     * so exchanging them later is a transfer rather than a translation.
     */
    fun stations(context: Context): List<Station> {
        val raw = prefs(context).getString(KEY_STATIONS, null)
        return Station.decode(raw)
    }

    fun setStations(context: Context, stations: List<Station>) {
        prefs(context).edit()
            .putString(KEY_STATIONS, Station.encode(stations))
            .apply()
    }
}
