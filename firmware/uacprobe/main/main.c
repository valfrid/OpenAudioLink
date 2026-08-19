/*
 * uacprobe — does an ESP32-S3 host a UAC 2.0 DAC, and can it play a tone?
 *
 * The smallest program that answers the two questions blocking
 * `docs/USB-AUDIO.md` track A. No Wi-Fi, no RTP, no discovery, no OTA,
 * nothing from the `oal_*` components. That is deliberate: this builds on
 * ESP-IDF 5.4 where the rest of the firmware builds on 5.3.1, and the whole
 * point of the isolation rules is that nothing shared gets dragged onto an
 * untested toolchain to satisfy an experiment.
 *
 * It runs in two phases, and the first matters more than the second:
 *
 *   1. ENUMERATE. Open the device, read the parsed descriptors back, and
 *      print the specific facts `docs/USB-AUDIO.md` has open questions
 *      about — UAC version, how many clock sources, whether any alternate
 *      setting carries a feedback endpoint, and whether 48 kHz / 24-bit /
 *      stereo is actually offered. These were read off Windows with USB
 *      Device Tree Viewer; this is the same reading taken by the machine
 *      that has to live with them.
 *
 *   2. TONE. Start the stream and write a 1 kHz sine, reporting how many
 *      frames have gone out. Run 18's lesson applies: an instrument that
 *      cannot distinguish the failure from the success is not an
 *      instrument, and "no sound" has to be separable from "no data".
 */

#include <inttypes.h>
#include <math.h>
#include <stdatomic.h>
#include <stdio.h>
#include <string.h>

#include "esp_log.h"
#include "esp_system.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "soc/rtc_cntl_reg.h"
#include "soc/soc.h"
#include "usb/uac2_desc.h"
#include "usb/uac2_host.h"
#include "usb/usb_host.h"

static const char *TAG = "uacprobe";

/* The RTP profile, so a success here is a success at the format the rest of
 * the system already speaks — no conversion hiding a mismatch. */
#define WANT_RATE        48000
#define WANT_CHANNELS    2
#define WANT_BITS        24

#define TONE_HZ          1000.0f
/* -14 dBFS. Loud enough to hear through a headphone amp, quiet enough not
 * to hurt whoever is wearing it when this works on the first try. */
#define TONE_AMPLITUDE   0.2f

/* 2 ms per write at 48 kHz. Small enough that a stall shows up promptly in
 * the frame counter, large enough not to spend the CPU on call overhead. */
#define CHUNK_FRAMES     96
#define CHUNK_BYTES      (CHUNK_FRAMES * WANT_CHANNELS * (WANT_BITS / 8))

#define HOST_TASK_STACK    4096
#define DRIVER_TASK_STACK  6144
#define PLAY_TASK_STACK    6144
#define DRIVER_TASK_PRIO   20
#define HOST_TASK_PRIO     21

typedef struct {
    uint8_t addr;
    uint8_t iface_num;
    volatile bool busy;
    _Atomic bool disconnected;
} probe_state_t;

static probe_state_t s_probe;

/* ── Phase 1: say what the device is ─────────────────────────────────── */

static const char *sync_type_name(uint8_t ep_attributes)
{
    /* Bits 3:2 of bmAttributes on an isochronous endpoint. This is the
     * field that decided the drift story in `docs/USB-AUDIO.md`: the
     * CX31993 dongle reads 0x0D — synchronous, no feedback endpoint — so
     * it follows the host's SOF instead of reporting its own rate. */
    switch ((ep_attributes >> 2) & 0x03) {
    case 0: return "no sync";
    case 1: return "asynchronous";
    case 2: return "adaptive";
    default: return "synchronous";
    }
}

static void report_device(uac2_host_device_handle_t dev)
{
    uac2_device_info_t info;

    if (uac2_host_device_get_info(dev, &info) != ESP_OK) {
        ESP_LOGE(TAG, "could not read parsed descriptors");
        return;
    }

    ESP_LOGI(TAG, "── device ──────────────────────────────────────────");
    ESP_LOGI(TAG, "VID:PID      %04X:%04X", info.vid, info.pid);
    ESP_LOGI(TAG, "manufacturer %s", info.manufacturer);
    ESP_LOGI(TAG, "product      %s", info.product);
    ESP_LOGI(TAG, "audio class  %s, bcdADC %04X",
             info.is_uac2 ? "UAC 2.0" : "not UAC 2.0", info.bcdADC);

    /* Open question 1. esp-uac2-host documents single-clock devices; the
     * dongle's Windows dump showed two (0x09 speaker, 0x0A microphone).
     * If only playback is used only the speaker's should matter, and this
     * line is where that stops being a hope. */
    ESP_LOGI(TAG, "── clocks: %u ──────────────────────────────────────",
             info.num_clock_sources);
    for (uint8_t i = 0; i < info.num_clock_sources; i++) {
        ESP_LOGI(TAG, "  clock 0x%02X attributes 0x%02X controls 0x%02X assoc terminal 0x%02X",
                 info.clock_sources[i].clock_id,
                 info.clock_sources[i].attributes,
                 info.clock_sources[i].controls,
                 info.clock_sources[i].assoc_terminal);
    }
    if (info.num_clock_sources > 1) {
        ESP_LOGW(TAG, "  more than one clock source — esp-uac2-host documents single-clock devices");
    }

    /* Open question 2, plus the format check. A feedback endpoint here
     * would mean the device reports its own rate and the host adapts; its
     * absence means the device follows our SOF, which is a different
     * drift story and the one decision 12 has to be reconciled with. */
    ESP_LOGI(TAG, "── streaming interfaces: %u ────────────────────────",
             info.num_as_ifaces);

    bool want_found = false;
    bool any_feedback = false;

    for (uint8_t i = 0; i < info.num_as_ifaces; i++) {
        const uac2_as_iface_t *as = &info.as_ifaces[i];

        ESP_LOGI(TAG, "  iface %u alt %u: %u ch, %u-bit (%u byte slots), "
                      "ep 0x%02X %s, wMaxPacketSize %u, bInterval %u, feedback ep 0x%02X",
                 as->interface_num, as->alt_setting, as->nr_channels,
                 as->bit_resolution, as->sub_slot_size,
                 as->ep_addr, sync_type_name(as->ep_attributes),
                 as->ep_max_packet_size, as->ep_interval, as->fb_ep_addr);

        if (as->fb_ep_addr != 0) {
            any_feedback = true;
        }
        if (as->nr_channels == WANT_CHANNELS && as->bit_resolution == WANT_BITS) {
            want_found = true;
        }
    }

    /* The parser's arrays are fixed size. The dongle's Windows dump has
     * five alternate settings carrying endpoints — three on the speaker
     * interface, two on the microphone — against a UAC2_MAX_AS_INTERFACES
     * of 4, so a truncated list here is a plausible outcome and not a
     * device that changed its mind. Say so rather than quietly reporting
     * whatever fitted. */
    if (info.num_as_ifaces >= UAC2_MAX_AS_INTERFACES) {
        ESP_LOGW(TAG, "  list is at the parser's limit of %d — some alternates may be missing",
                 UAC2_MAX_AS_INTERFACES);
    }

    ESP_LOGI(TAG, "── verdict ─────────────────────────────────────────");
    ESP_LOGI(TAG, "%u ch / %u-bit offered: %s",
             WANT_CHANNELS, WANT_BITS, want_found ? "yes" : "NO");
    ESP_LOGI(TAG, "feedback endpoint: %s",
             any_feedback ? "yes — device reports its rate, host adapts"
                          : "no — device follows the host's SOF");

    /* Everything the driver knows, in its own words, in case the summary
     * above is asking the wrong questions. */
    uac2_host_device_print_info(dev);
}

/* ── Phase 2: make a noise ───────────────────────────────────────────── */

static void fill_sine(uint8_t *dst, size_t frames, float *phase)
{
    const float step = 2.0f * (float)M_PI * TONE_HZ / (float)WANT_RATE;

    for (size_t i = 0; i < frames; i++) {
        int32_t sample = (int32_t)(sinf(*phase) * TONE_AMPLITUDE * 8388607.0f);

        /* 24-bit little-endian, same value in both channels. */
        dst[0] = dst[3] = (uint8_t)(sample & 0xFF);
        dst[1] = dst[4] = (uint8_t)((sample >> 8) & 0xFF);
        dst[2] = dst[5] = (uint8_t)((sample >> 16) & 0xFF);
        dst += 6;

        *phase += step;
        if (*phase >= 2.0f * (float)M_PI) {
            *phase -= 2.0f * (float)M_PI;
        }
    }
}

static void device_event_cb(uac2_host_device_handle_t dev,
                            const uac2_host_device_event_t event, void *arg)
{
    (void)dev;
    probe_state_t *probe = (probe_state_t *)arg;

    /* Runs on the USB host event task and must not block, so it sets a
     * flag and nothing else. Closing the handle from here deadlocks. */
    if (event == UAC2_HOST_DEVICE_EVENT_DISCONNECTED ||
        event == UAC2_HOST_DEVICE_EVENT_STREAM_ERROR) {
        atomic_store(&probe->disconnected, true);
    }
}

static void probe_task(void *arg)
{
    probe_state_t *probe = (probe_state_t *)arg;
    uac2_host_device_handle_t dev = NULL;
    uint8_t pcm[CHUNK_BYTES];
    float phase = 0.0f;
    uint64_t frames_written = 0;
    int64_t next_report_us = 0;

    uac2_host_device_config_t dev_cfg = {
        .addr = probe->addr,
        .iface_num = probe->iface_num,
        .buffer_size = 0,       /* auto, about 100 ms */
        .buffer_threshold = 0,  /* auto, half */
        .callback = device_event_cb,
        .callback_arg = probe,
    };

    esp_err_t err = uac2_host_device_open(&dev_cfg, &dev);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "device_open failed: %s", esp_err_to_name(err));
        goto done;
    }

    report_device(dev);

    /* Asking the device its rate before and after setting ours turns a
     * silent refusal into a visible one — with two clock sources present,
     * a set that lands on the wrong clock is exactly the failure worth
     * catching. */
    uint32_t rate = 0;
    if (uac2_host_device_get_sample_rate(dev, &rate) == ESP_OK) {
        ESP_LOGI(TAG, "sample rate before start: %" PRIu32 " Hz", rate);
    }

    uac2_host_stream_config_t stream_cfg = {
        .channels = WANT_CHANNELS,
        .bit_resolution = WANT_BITS,
        .sample_freq = WANT_RATE,
        .flags = 0,
    };

    err = uac2_host_device_start(dev, &stream_cfg);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "device_start failed: %s — the format above is the first thing to doubt",
                 esp_err_to_name(err));
        goto close;
    }

    if (uac2_host_device_get_sample_rate(dev, &rate) == ESP_OK) {
        ESP_LOGI(TAG, "sample rate after start:  %" PRIu32 " Hz%s", rate,
                 rate == WANT_RATE ? "" : "  ← not what was asked for");
    }

    bool clock_valid = false;
    if (uac2_host_device_get_clock_valid(dev, &clock_valid) == ESP_OK) {
        ESP_LOGI(TAG, "clock valid: %s", clock_valid ? "yes" : "NO — device is not locked");
    }

    ESP_LOGI(TAG, "playing %g Hz at %d Hz / %d-bit / %d ch",
             TONE_HZ, WANT_RATE, WANT_BITS, WANT_CHANNELS);

    while (!atomic_load(&probe->disconnected)) {
        fill_sine(pcm, CHUNK_FRAMES, &phase);

        err = uac2_host_device_write(dev, pcm, sizeof(pcm), 1000);
        if (err != ESP_OK) {
            if (atomic_load(&probe->disconnected)) {
                break;
            }
            ESP_LOGW(TAG, "device_write failed after %llu frames: %s",
                     (unsigned long long)frames_written, esp_err_to_name(err));
            break;
        }

        frames_written += CHUNK_FRAMES;

        /* A running total once a second. Silence with a rising count is a
         * different fault from silence with a stuck one, and only one of
         * them is about the wire. */
        int64_t now_us = esp_timer_get_time();
        if (now_us >= next_report_us) {
            next_report_us = now_us + 1000000;
            ESP_LOGI(TAG, "%llu frames written (%.1f s of audio)",
                     (unsigned long long)frames_written,
                     (double)frames_written / WANT_RATE);
        }
    }

    ESP_LOGI(TAG, "stopping after %llu frames", (unsigned long long)frames_written);

close:
    (void)uac2_host_device_close(dev);
done:
    probe->busy = false;
    atomic_store(&probe->disconnected, false);
    vTaskDelete(NULL);
}

/* ── Getting back into download mode without touching the board ──────── */

/*
 * Type `download` on the serial console and the board reboots ready to be
 * flashed.
 *
 * The obvious hardware answer does not work here. esptool normally enters
 * download mode by driving two lines — one to reset the chip, one to hold
 * GPIO0 low while it comes up — and on a XIAO ESP32S3 **GPIO0 is not
 * brought out anywhere**. It reaches the BOOT button and nothing else. So
 * wiring the adapter to EN buys an automatic *reset* and still leaves a
 * finger on BOOT, which is most of the inconvenience.
 *
 * The chip has a second route to the same place: a bit in an RTC register
 * that tells the ROM to enter download mode on the next boot whatever GPIO0
 * is doing. Set it, restart, and the board arrives in download mode with no
 * buttons and no extra wires — over the UART that is already connected to
 * read this log.
 *
 * The trigger is a word rather than a keystroke because the ESP's RX line
 * may well be floating: a single character would make electrical noise
 * enough to reboot the experiment.
 */
static void download_watch_task(void *arg)
{
    static const char phrase[] = "download";
    size_t matched = 0;

    (void)arg;
    setvbuf(stdin, NULL, _IONBF, 0);

    while (true) {
        int c = fgetc(stdin);
        if (c == EOF) {
            vTaskDelay(pdMS_TO_TICKS(50));
            continue;
        }

        if (c == phrase[matched]) {
            matched++;
        } else {
            matched = (c == phrase[0]) ? 1 : 0;
        }

        if (matched == sizeof(phrase) - 1) {
            ESP_LOGW(TAG, "rebooting into download mode — flash now");
            fflush(stdout);
            vTaskDelay(pdMS_TO_TICKS(100));  /* let the line drain */
            REG_WRITE(RTC_CNTL_OPTION1_REG, RTC_CNTL_FORCE_DOWNLOAD_BOOT);
            esp_restart();
        }
    }
}

/* ── Plumbing ────────────────────────────────────────────────────────── */

static void driver_event_cb(uint8_t addr, uint8_t iface_num,
                            const uac2_host_driver_event_t event, void *arg)
{
    probe_state_t *probe = (probe_state_t *)arg;

    if (event == UAC2_HOST_DRIVER_EVENT_RX_CONNECTED) {
        /* The dongle has a microphone path too. Noted and left alone —
         * `docs/LISTENING.md` wants it eventually, this program does not. */
        ESP_LOGI(TAG, "capture interface present: addr=%u iface=%u (ignored)",
                 addr, iface_num);
        return;
    }

    if (probe->busy) {
        return;
    }

    probe->addr = addr;
    probe->iface_num = iface_num;
    probe->busy = true;
    atomic_store(&probe->disconnected, false);

    ESP_LOGI(TAG, "playback interface connected: addr=%u iface=%u", addr, iface_num);

    if (xTaskCreatePinnedToCore(probe_task, "uacprobe", PLAY_TASK_STACK, probe,
                                DRIVER_TASK_PRIO + 1, NULL, 0) != pdPASS) {
        ESP_LOGE(TAG, "could not start the probe task");
        probe->busy = false;
    }
}

static void usb_host_task(void *arg)
{
    TaskHandle_t caller = (TaskHandle_t)arg;
    usb_host_config_t host_config = {
        .skip_phy_setup = false,
        .intr_flags = ESP_INTR_FLAG_LEVEL1,
    };

    ESP_ERROR_CHECK(usb_host_install(&host_config));
    xTaskNotifyGive(caller);

    while (true) {
        uint32_t flags = 0;
        usb_host_lib_handle_events(portMAX_DELAY, &flags);

        if (flags & USB_HOST_LIB_EVENT_FLAGS_NO_CLIENTS) {
            usb_host_device_free_all();
        }
    }
}

void app_main(void)
{
    memset(&s_probe, 0, sizeof(s_probe));

    ESP_LOGI(TAG, "uacprobe: USB host + UAC 2.0, 48 kHz / 24-bit / stereo tone");
    ESP_LOGI(TAG, "if nothing follows, the device is not attaching — check VBUS first");
    /* Printed here because the person reading this log is exactly the person
     * about to want it. Once the host driver below claims the USB peripheral
     * the board stops appearing as a serial port, so the usual automatic
     * reset into download mode has nothing to talk to. */
    ESP_LOGI(TAG, "to reflash: type 'download' here, or hold BOOT and tap RESET");

    xTaskCreate(download_watch_task, "dl_watch", 3072, NULL, 2, NULL);

    xTaskCreatePinnedToCore(usb_host_task, "usb_host", HOST_TASK_STACK,
                            xTaskGetCurrentTaskHandle(), HOST_TASK_PRIO, NULL, 0);
    ulTaskNotifyTake(pdTRUE, pdMS_TO_TICKS(1000));

    uac2_host_driver_config_t driver_cfg = {
        .create_background_task = true,
        .task_priority = DRIVER_TASK_PRIO,
        .stack_size = DRIVER_TASK_STACK,
        .core_id = 0,
        .callback = driver_event_cb,
        .callback_arg = &s_probe,
    };

    ESP_ERROR_CHECK(uac2_host_install(&driver_cfg));
    ESP_LOGI(TAG, "waiting for a UAC 2.0 device");
}
