/*
 * The USB sink: a UAC 2.0 dongle the node hosts (docs/USB-AUDIO.md).
 *
 * Proven by `firmware/uacprobe` before any of this existed — an ESP32-S3
 * hosted a CX31993 dongle and played a tone through it for ten minutes
 * without an error, at a rate locked to within 20 ppm of the node's own
 * clock. This is that program's second half, wearing the sink interface.
 *
 * One thing here is genuinely different from I²S, and the interface exists
 * because of it: **a USB device arrives when somebody plugs it in.** I²S
 * pins are there at boot whether or not anything is soldered to them. A
 * dongle may be attached minutes later, or unplugged mid-song, or never
 * present at all. So this sink opens successfully with nothing attached and
 * reports `ready` false until a device has been enumerated and its stream
 * started — and playout leaves the ring alone while that is so, rather than
 * draining it into nowhere and counting the frames as played.
 *
 * The samples the ring holds are 32-bit slots carrying 24-bit values
 * left-justified, which is what I²S wants and is not what USB wants: the
 * dongle's 24-bit alternate setting takes three packed bytes per sample.
 * Converting is this file's other job.
 */

#include "oal_sink.h"

#include <stdio.h>
#include <string.h>

#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "oal_rtp.h"
#include "usb/uac2_host.h"
#include "usb/usb_host.h"

static const char *TAG = "oal_sink_usb";

#define HOST_TASK_STACK    4096
#define DRIVER_TASK_STACK  6144
#define DRIVER_TASK_PRIO   20
#define HOST_TASK_PRIO     21

/*
 * Core 1, and this is not a detail.
 *
 * Wi-Fi runs on core 0. Isochronous USB wants servicing every millisecond
 * at a priority above almost everything, and putting the two on one core
 * makes them compete — measured as 1.09 % packet loss in *bursts* at
 * −47 dBm, against 0.005 % and isolated single gaps on an I²S node in the
 * same house. Decision 2 anticipated exactly this and said so about I²S
 * DMA: "its second core matters here: I2S DMA servicing can run apart from
 * Wi-Fi transmit bursts." A USB host is the same argument, louder.
 *
 * The interrupt matters as much as the tasks. ESP-IDF allocates an
 * interrupt on whichever core calls the allocating function, so
 * `usb_host_install` is called from the host task below rather than from
 * `usb_open` — otherwise the ISR lands on whatever core started playout,
 * which is core 0, and pinning the tasks alone would leave the loudest
 * part of the load where it started.
 */
#define USB_CORE           1

/* The dongle's 24-bit alternate: three bytes a sample, two channels. */
#define USB_BYTES_PER_FRAME (3 * OAL_RTP_CHANNELS)

/* Enough for one playout chunk. 240 frames is the 5 ms the RTP packet
 * carries and the depth the I²S path uses; sized here so a chunk never
 * needs splitting on its way through the conversion. */
#define PACK_FRAMES  240
#define PACK_BYTES   (PACK_FRAMES * USB_BYTES_PER_FRAME)

static uac2_host_device_handle_t s_dev;
static volatile bool s_streaming;
static uint32_t s_sample_rate;
static uint8_t s_pack[PACK_BYTES];

/* Set from the USB event task, acted on by the open-device task. Neither
 * may block the other: the driver's callbacks run on its client event task
 * and deadlock if they call back into it. */
static volatile uint8_t s_pending_addr;
static volatile uint8_t s_pending_iface;
static volatile bool s_pending;
static volatile esp_err_t s_install_result;

static void device_event_cb(uac2_host_device_handle_t dev,
                            const uac2_host_device_event_t event, void *arg)
{
    (void)dev;
    (void)arg;

    if (event == UAC2_HOST_DEVICE_EVENT_DISCONNECTED ||
        event == UAC2_HOST_DEVICE_EVENT_STREAM_ERROR) {
        /* A flag and nothing else. Closing the handle from here deadlocks;
         * the attach task below does it. */
        s_streaming = false;
    }
}

static void driver_event_cb(uint8_t addr, uint8_t iface_num,
                            const uac2_host_driver_event_t event, void *arg)
{
    (void)arg;

    if (event != UAC2_HOST_DRIVER_EVENT_TX_CONNECTED) {
        /* The dongle's microphone. `docs/LISTENING.md` wants it eventually;
         * a Consumer does not. */
        return;
    }
    if (s_pending || s_streaming) {
        return;
    }

    s_pending_addr = addr;
    s_pending_iface = iface_num;
    s_pending = true;
}

/*
 * Opening and closing live here because both are forbidden from the
 * driver's callbacks, and because a dongle can come and go for as long as
 * the node is powered.
 */
static void attach_task(void *arg)
{
    (void)arg;

    for (;;) {
        if (s_pending && !s_streaming) {
            uac2_host_device_config_t dev_cfg = {
                .addr = s_pending_addr,
                .iface_num = s_pending_iface,
                .buffer_size = 0,       /* auto, about 100 ms */
                .buffer_threshold = 0,
                .callback = device_event_cb,
                .callback_arg = NULL,
            };

            esp_err_t err = uac2_host_device_open(&dev_cfg, &s_dev);
            if (err != ESP_OK) {
                ESP_LOGE(TAG, "device_open failed: %s", esp_err_to_name(err));
                s_pending = false;
                continue;
            }

            uac2_host_stream_config_t stream_cfg = {
                .channels = OAL_RTP_CHANNELS,
                .bit_resolution = 24,
                .sample_freq = s_sample_rate,
                .flags = 0,
            };

            err = uac2_host_device_start(s_dev, &stream_cfg);
            if (err != ESP_OK) {
                ESP_LOGE(TAG, "device_start failed: %s — the dongle may not "
                              "offer %" PRIu32 " Hz / 24-bit / stereo",
                         esp_err_to_name(err), s_sample_rate);
                uac2_host_device_close(s_dev);
                s_dev = NULL;
                s_pending = false;
                continue;
            }

            ESP_LOGI(TAG, "dongle playing: %" PRIu32 " Hz, 24-bit, %d ch",
                     s_sample_rate, OAL_RTP_CHANNELS);
            s_pending = false;
            s_streaming = true;
        }

        /* A device that went away while we held it. Closing has to happen
         * off the callback, so it happens here. */
        if (!s_streaming && s_dev != NULL) {
            ESP_LOGW(TAG, "dongle gone; waiting for one to be plugged in");
            uac2_host_device_close(s_dev);
            s_dev = NULL;
        }

        vTaskDelay(pdMS_TO_TICKS(100));
    }
}

static void usb_host_task(void *arg)
{
    TaskHandle_t caller = (TaskHandle_t)arg;

    /* Installed here, on this core, so the USB interrupt is allocated on
     * core 1 with the tasks that service it. */
    usb_host_config_t host_config = {
        .skip_phy_setup = false,
        .intr_flags = ESP_INTR_FLAG_LEVEL1,
    };

    esp_err_t err = usb_host_install(&host_config);
    s_install_result = err;
    xTaskNotifyGive(caller);

    if (err != ESP_OK) {
        vTaskDelete(NULL);
        return;
    }

    for (;;) {
        uint32_t flags = 0;
        usb_host_lib_handle_events(portMAX_DELAY, &flags);

        if (flags & USB_HOST_LIB_EVENT_FLAGS_NO_CLIENTS) {
            usb_host_device_free_all();
        }
    }
}

static esp_err_t usb_open(const oal_sink_config_t *config)
{
    s_sample_rate = config->sample_rate;

    /*
     * The console is about to go, and saying so is the last chance to.
     *
     * A node's console runs over USB-Serial/JTAG, because UART0 is not
     * wired to the connector on these boards. The ESP32-S3 shares one PHY
     * between USB-Serial/JTAG and USB-OTG, so installing the host stack
     * takes the pins and the log stops here — permanently, on this boot.
     *
     * That is the price of the dongle output stage and it is worth paying
     * on a deployed node: /status, OTA and the Hub are the diagnostics
     * that matter once a speaker is on a shelf, and all three survive.
     * But somebody watching a serial monitor deserves to know that the
     * silence about to follow is expected rather than a crash.
     */
    ESP_LOGW(TAG, "installing the USB host stack — the console goes quiet now.");
    ESP_LOGW(TAG, "use /status, the Hub or a UART adapter on D6 from here on.");
    fflush(stdout);
    vTaskDelay(pdMS_TO_TICKS(200));

    if (xTaskCreatePinnedToCore(usb_host_task, "usb_host", HOST_TASK_STACK,
                               xTaskGetCurrentTaskHandle(), HOST_TASK_PRIO,
                               NULL, USB_CORE) != pdPASS) {
        return ESP_ERR_NO_MEM;
    }

    /* Wait for it to install, so a failure is reported from here rather
     * than discovered later as a device that never arrives. */
    ulTaskNotifyTake(pdTRUE, pdMS_TO_TICKS(2000));
    if (s_install_result != ESP_OK) {
        ESP_LOGE(TAG, "usb_host_install failed: %s", esp_err_to_name(s_install_result));
        return s_install_result;
    }

    esp_err_t err;

    uac2_host_driver_config_t driver_cfg = {
        .create_background_task = true,
        .task_priority = DRIVER_TASK_PRIO,
        .stack_size = DRIVER_TASK_STACK,
        .core_id = USB_CORE,
        .callback = driver_event_cb,
        .callback_arg = NULL,
    };

    err = uac2_host_install(&driver_cfg);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "uac2_host_install failed: %s", esp_err_to_name(err));
        return err;
    }

    if (xTaskCreatePinnedToCore(attach_task, "oal_usb_attach", 4096, NULL, 6,
                               NULL, USB_CORE) != pdPASS) {
        return ESP_ERR_NO_MEM;
    }

    /*
     * Open, and deliberately not ready. Nothing may be plugged in yet, and
     * that is a normal state for this backend rather than a failure to
     * report — a node whose dongle arrives ten minutes after it boots
     * should start playing then, not require a reboot.
     */
    ESP_LOGI(TAG, "USB host up, waiting for a UAC 2.0 dongle");
    return ESP_OK;
}

static void usb_close(void)
{
    if (s_dev != NULL) {
        uac2_host_device_close(s_dev);
        s_dev = NULL;
    }
    s_streaming = false;
}

static bool usb_ready(void)
{
    return s_streaming && s_dev != NULL;
}

/*
 * 32-bit left-justified slots in, 24-bit packed out.
 *
 * The ring holds what I²S wants: the sample in the top three bytes of a
 * 32-bit word. USB wants those three bytes and not the padding. Little
 * endian on both sides, so the conversion is dropping byte 0 of each word
 * — which is the low byte, and it is zero.
 */
static esp_err_t usb_write(const void *data, size_t bytes, size_t *written,
                           uint32_t timeout_ms)
{
    if (!usb_ready()) {
        *written = 0;
        return ESP_ERR_INVALID_STATE;
    }

    const int32_t *samples = (const int32_t *)data;
    size_t sample_count = bytes / sizeof(int32_t);
    if (sample_count > PACK_BYTES / 3) {
        sample_count = PACK_BYTES / 3;
    }

    for (size_t i = 0; i < sample_count; i++) {
        uint32_t slot = (uint32_t)samples[i];
        s_pack[i * 3 + 0] = (uint8_t)(slot >> 8);
        s_pack[i * 3 + 1] = (uint8_t)(slot >> 16);
        s_pack[i * 3 + 2] = (uint8_t)(slot >> 24);
    }

    esp_err_t err = uac2_host_device_write(s_dev, s_pack, sample_count * 3,
                                           timeout_ms);
    if (err != ESP_OK) {
        *written = 0;
        return err;
    }

    /* Report progress in the caller's units — the bytes it handed over,
     * not the bytes that went down the wire. */
    *written = sample_count * sizeof(int32_t);
    return ESP_OK;
}

const oal_sink_t *oal_sink_usb(void)
{
    static const oal_sink_t sink = {
        .name = "USB",
        .open = usb_open,
        .close = usb_close,
        .ready = usb_ready,
        .write = usb_write,
    };
    return &sink;
}
