package org.openaudiolink.phone

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.IBinder
import android.provider.Settings
import androidx.core.app.NotificationCompat

/**
 * The service that keeps the music going when the screen is off.
 *
 * Not optional and not a formality. Android freezes a background process,
 * and 5 ms packets do not survive that — a phone in a pocket would stop
 * the party the moment the screen locked. A foreground service with a
 * visible notification is the only arrangement in which a phone may keep
 * sending, and the notification is honest: something is using the radio
 * and the battery, and a person should be able to see it and stop it.
 */
class ProducerService : Service() {

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        createChannel()
        startForeground(NOTIFICATION_ID, notification("Looking for speakers"))

        Producer.attach(this, Producer.identity(deviceName(), deviceId()))

        // Redraw the notification as the state changes, so the lock screen
        // says what is playing and where.
        Thread({
            while (true) {
                val state = Producer.state.value
                val text = when {
                    !state.streaming -> "Ready — ${state.speakers.size} speaker(s) found"
                    else -> "${state.sourceLabel} → ${state.selected.size} speaker(s)"
                }
                notificationManager().notify(NOTIFICATION_ID, notification(text))
                Thread.sleep(2_000)
            }
        }, "oal-notification").apply { isDaemon = true }.start()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            Producer.stopStream()
            stopSelf()
            return START_NOT_STICKY
        }
        // Restarted by the system without an intent: come back up, because
        // a party that ends because the phone was low on memory is worse
        // than one that resumes silently and waits to be told what to play.
        return START_STICKY
    }

    override fun onDestroy() {
        Producer.detach()
        super.onDestroy()
    }

    private fun notification(text: String): Notification {
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE,
        )
        val stop = PendingIntent.getService(
            this, 1, Intent(this, ProducerService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE,
        )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle(getString(R.string.app_name))
            .setContentText(text)
            .setSmallIcon(android.R.drawable.stat_sys_upload)
            .setContentIntent(open)
            .addAction(android.R.drawable.ic_media_pause, "Stop", stop)
            .setOngoing(true)
            .setSilent(true)
            .build()
    }

    private fun createChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val channel = NotificationChannel(
            CHANNEL_ID,
            "Streaming",
            NotificationManager.IMPORTANCE_LOW,   // no sound; it is about sound
        ).apply { description = "Shown while this phone is sending audio to speakers" }
        notificationManager().createNotificationChannel(channel)
    }

    private fun notificationManager() =
        getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager

    /** One name for the announce and for the Spotify cast point. */
    private fun deviceName(): String = Producer.castPointName(this)

    /**
     * A stable identity for this install.
     *
     * `ANDROID_ID` is per app and per device and survives a reboot, which
     * is what discovery needs: a producer that changed its id every launch
     * would appear as a new device in every node's peer table and never
     * leave the old ones.
     */
    @Suppress("HardwareIds")
    private fun deviceId(): String {
        val raw = Settings.Secure.getString(contentResolver, Settings.Secure.ANDROID_ID)
        return "phone-" + (raw ?: "unknown")
    }

    companion object {
        const val CHANNEL_ID = "oal-streaming"
        const val NOTIFICATION_ID = 1
        const val ACTION_STOP = "org.openaudiolink.phone.STOP"

        fun start(context: Context) {
            val intent = Intent(context, ProducerService::class.java)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(intent)
            } else {
                context.startService(intent)
            }
        }
    }
}
