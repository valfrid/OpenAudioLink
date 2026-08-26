/*
 * OpenAudioLink test node.
 *
 * "Hello world" milestone: boot -> Wi-Fi (NVS credentials or setup portal)
 * -> discovery announce -> control server -> OTA-updatable over the Hub.
 *
 * Runs on the ESP32-S3 reference platform; it contains no
 * hardware-profile-specific code.
 */

#include <string.h>

#include "esp_app_desc.h"
#include "esp_ota_ops.h"
#include "esp_log.h"
#include "esp_mac.h"
#include "esp_netif.h"
#include "esp_system.h"
#include "esp_timer.h"
#include "esp_wifi.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "nvs_flash.h"
#include "sdkconfig.h"

#include "oal_config.h"
#include "oal_control.h"
#include "oal_join.h"
#include "oal_discovery.h"
#include "oal_capture.h"
#include "oal_playout.h"
#include "oal_stream.h"
#include "oal_wifi.h"

static const char *TAG = "oal_testnode";

/*
 * The version lives in version.txt, which ESP-IDF stamps into the image
 * header as PROJECT_VER. Reading it back from the header rather than
 * keeping a #define here means the version a node announces and the
 * version carried by its firmware image cannot drift apart — so the Hub
 * can tell you what an uploaded image actually contains before you
 * install it.
 */
#define FIRMWARE_VERSION (esp_app_get_description()->version)

#if CONFIG_IDF_TARGET_ESP32S3
#define HARDWARE_PROFILE "esp32s3-devkit"
#else
/* Not a supported target, but the build should say so rather than
 * silently announcing a profile that does not describe the board. */
#define HARDWARE_PROFILE CONFIG_IDF_TARGET "-unsupported"
#endif

/*
 * A periodic sign of life. USB Serial/JTAG keeps no buffer for a terminal
 * that attaches after boot, so without this a console opened at any point
 * during normal running stays completely blank — indistinguishable from a
 * dead board.
 */
static void heartbeat_task(void *arg)
{
    for (;;) {
        wifi_mode_t mode = WIFI_MODE_NULL;
        esp_wifi_get_mode(&mode);

        /* The state, not just a mode number: catching the boot log over
         * native USB is unreliable because the port dies with every reset,
         * so everything needed to diagnose the node is repeated here. */
        char detail[160] = "";
        if (mode == WIFI_MODE_AP || mode == WIFI_MODE_APSTA) {
            wifi_config_t cfg;
            if (esp_wifi_get_config(WIFI_IF_AP, &cfg) == ESP_OK) {
                wifi_sta_list_t stations;
                int clients = (esp_wifi_ap_get_sta_list(&stations) == ESP_OK)
                    ? (int)stations.num : -1;
                snprintf(detail, sizeof(detail), " | AP \"%s\" ch %d, %d client(s)",
                         (char *)cfg.ap.ssid, (int)cfg.ap.channel, clients);
            } else {
                snprintf(detail, sizeof(detail), " | AP mode but no config readable");
            }
        } else if (mode == WIFI_MODE_STA) {
            wifi_ap_record_t ap;
            if (esp_wifi_sta_get_ap_info(&ap) == ESP_OK) {
                esp_netif_ip_info_t ip = { 0 };
                esp_netif_t *netif = esp_netif_get_handle_from_ifkey("WIFI_STA_DEF");
                if (netif != NULL) {
                    esp_netif_get_ip_info(netif, &ip);
                }
                /* The BSSID, not just the SSID: in a mesh every node
                 * advertises the same name, so the BSSID is the only way
                 * to tell which one this is and whether a weak signal
                 * means "far from the right node" or "on the wrong one". */
                snprintf(detail, sizeof(detail),
                         " | joined \"%s\" bssid %02x:%02x:%02x:%02x:%02x:%02x"
                         " ch %d rssi %d, ip " IPSTR,
                         (char *)ap.ssid, ap.bssid[0], ap.bssid[1], ap.bssid[2],
                         ap.bssid[3], ap.bssid[4], ap.bssid[5],
                         (int)ap.primary, ap.rssi, IP2STR(&ip.ip));
            } else {
                snprintf(detail, sizeof(detail), " | STA mode, not joined");
            }
        }

        ESP_LOGI(TAG, "alive: uptime %llus, heap %u, mode %d%s",
                 (unsigned long long)(esp_timer_get_time() / 1000000),
                 (unsigned int)esp_get_free_heap_size(), (int)mode, detail);
        vTaskDelay(pdMS_TO_TICKS(5000));
    }
}

/*
 * Confirming a freshly installed image, and the trap in doing it wrong.
 *
 * With rollback enabled a new image boots in PENDING_VERIFY and must call
 * `esp_ota_mark_app_valid_cancel_rollback()` or the bootloader reverts to
 * the other slot at the next boot. That is the protection: an image that
 * panics, hangs or cannot bring its memory up undoes itself, with no cable
 * and no Hub.
 *
 * It is also the danger. An image that *never* calls it reverts every
 * time, including a perfectly good one, so a misplaced call here silently
 * un-installs every future update.
 *
 * The bar is deliberately "this image can run", not "this image works".
 * Thirty seconds after every subsystem has started, with no reboot in
 * between, catches boot loops, panics during startup and a memory map that
 * will not come up -- which is the whole reason this exists.
 *
 * It deliberately does not wait for Wi-Fi. Tying confirmation to the
 * network would revert a sound image because a router happened to be down
 * when a node rebooted, and nothing about that is a firmware fault.
 *
 * And it must not be stricter still. "Confirm only if the output stage
 * came up" is tempting and would roll back forever on a USB node with no
 * dongle plugged in.
 */
#define OTA_CONFIRM_DELAY_MS 30000

static void ota_confirm_task(void *arg)
{
    (void)arg;
    vTaskDelay(pdMS_TO_TICKS(OTA_CONFIRM_DELAY_MS));

    const esp_partition_t *running = esp_ota_get_running_partition();
    esp_ota_img_states_t state;
    if (running != NULL && esp_ota_get_state_partition(running, &state) == ESP_OK
            && state == ESP_OTA_IMG_PENDING_VERIFY) {
        if (esp_ota_mark_app_valid_cancel_rollback() == ESP_OK) {
            ESP_LOGW(TAG, "image confirmed after %d s; rollback cancelled",
                     OTA_CONFIRM_DELAY_MS / 1000);
        } else {
            ESP_LOGE(TAG, "could not confirm this image; it will revert on reboot");
        }
    }
    vTaskDelete(NULL);
}

static void confirm_image_later(void)
{
    xTaskCreate(ota_confirm_task, "oal_ota_confirm", 3072, NULL, 3, NULL);
}

void app_main(void)
{
    esp_err_t err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        /* Loud on purpose: this wipes stored Wi-Fi credentials, so if it
         * happens on every boot the device can never be provisioned. */
        ESP_LOGW(TAG, "erasing NVS (%s) — stored configuration is lost", esp_err_to_name(err));
        ESP_ERROR_CHECK(nvs_flash_erase());
        err = nvs_flash_init();
    }
    ESP_ERROR_CHECK(err);

    ESP_LOGI(TAG, "OpenAudioLink test node %s (%s)", FIRMWARE_VERSION, HARDWARE_PROFILE);

    /* Started before the network so a board that fails during Wi-Fi bring-up
     * still reports that it is alive. */
    xTaskCreate(heartbeat_task, "oal_heartbeat", 3072, NULL, 1, NULL);

    if (oal_wifi_start(CONFIG_OAL_WIFI_SSID, CONFIG_OAL_WIFI_PASSWORD) == OAL_WIFI_PORTAL) {
        /* Provisioning portal is running; normal operation starts after the
         * user saves credentials and the device reboots. */
        return;
    }

    /* Factory identity from the Wi-Fi MAC; see protocol/IDENTITY.md.
     * Provisioned identities arrive with USB provisioning in Phase 2.5. */
    uint8_t mac[6];
    ESP_ERROR_CHECK(esp_read_mac(mac, ESP_MAC_WIFI_STA));

    /* Roles come from NVS, not from this binary (decision 5): two
     * identical boards become a producer and a consumer by configuration.
     * Unset means consumer, which is the common case. */
    oal_roles_t roles = oal_config_get_roles();
    char role_names[OAL_ROLES_STR_MAX];
    oal_roles_to_list(roles, role_names, sizeof(role_names));
    ESP_LOGI(TAG, "roles: %s", role_names);

    static oal_discovery_config_t discovery = {
        .hardware_profile = HARDWARE_PROFILE,
    };
    discovery.roles = roles;
    /* Not a static initialiser: the version is read from the image header
     * at run time. It points into the header, which stays mapped. */
    discovery.firmware_version = FIRMWARE_VERSION;
    snprintf(discovery.id, sizeof(discovery.id), "mac-%02x%02x%02x%02x%02x%02x",
             mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
    /* A name given at provisioning wins. Failing that, the MAC suffix:
     * identical boards run identical images (decision 5), so without it
     * every node in the device list is called the same thing and there is
     * no way to tell which is which. The same three bytes name the setup
     * access point, so an unnamed node is recognisably the one just
     * provisioned. */
    if (oal_config_get_name(discovery.name, sizeof(discovery.name)) != ESP_OK) {
        snprintf(discovery.name, sizeof(discovery.name), "%s-%02X%02X%02X",
                 CONFIG_OAL_NODE_NAME, mac[3], mac[4], mac[5]);
    }

    static oal_control_config_t control;
    control.id = discovery.id;
    control.name = discovery.name;
    control.roles = discovery.roles;
    control.hardware_profile = discovery.hardware_profile;
    control.firmware_version = discovery.firmware_version;

    ESP_ERROR_CHECK(oal_control_start(&control));
    ESP_ERROR_CHECK(oal_discovery_start(&discovery));

    /* A consumer listens from boot. It has nothing to configure, and a
     * receiver that must be armed before it can be sent to turns every
     * producer start into a race. A producer waits to be told where to
     * send, which it cannot know by itself. */
#if CONFIG_OAL_ADC_ENABLED
    /* The Analog Source. Started for a Producer whether or not a stream is
     * running: an ADC brought up with the stream would deliver its first
     * packets from a peripheral that has not settled, and that is audible.
     * A failure is survivable — the synthetic sources still work, which is
     * what the link measurements use. */
    if ((roles & OAL_ROLE_PRODUCER) != 0) {
        /*
         * Which input this box is wired to, read at boot like the output
         * stage and the roles.
         *
         * It selects a set of pins *and* which end of the I2S bus drives
         * the clocks, so it cannot be a runtime switch: the microphone is a
         * slave and needs BCK and WS supplied, while the self-clocked
         * PCM1808 module supplies its own and makes this end the follower.
         * The two therefore get separate pins -- sharing them would put two
         * drivers on one line, and HARDWARE.md records that the symptom of
         * that is silence with nothing to say why.
         *
         * One box, two jobs: a microphone at the listening position for
         * room measurement, a line input by the turntable, never at once.
         * Not worth a second ESP32 (docs/ROOM-CALIBRATION.md).
         */
        oal_capture_config_t capture = {
            .bclk_gpio   = CONFIG_OAL_ADC_BCLK_GPIO,
            .ws_gpio     = CONFIG_OAL_ADC_WS_GPIO,
            .din_gpio    = CONFIG_OAL_ADC_DIN_GPIO,
            .mclk_gpio   = CONFIG_OAL_ADC_MCLK_GPIO,
            .slave       = CONFIG_OAL_ADC_SLAVE,
            .sample_rate = OAL_RTP_SAMPLE_RATE,
        };

        const oal_input_t stage = oal_config_get_input();
        if (stage == OAL_INPUT_MIC) {
            capture.bclk_gpio = CONFIG_OAL_MIC_BCLK_GPIO;
            capture.ws_gpio   = CONFIG_OAL_MIC_WS_GPIO;
            capture.din_gpio  = CONFIG_OAL_MIC_DIN_GPIO;
            /* No master clock: the ICS-43434 wants none, and this end
             * generates the two it does want. */
            capture.mclk_gpio = -1;
            capture.slave     = false;
        }
        ESP_LOGI(TAG, "capturing from %s (%s)", oal_input_name(stage),
                 capture.slave ? "following the module's clock"
                               : "clocking it from here");

        esp_err_t input = oal_capture_start(&capture);
        if (input == ESP_OK) {
            oal_stream_producer_set_source(oal_capture_read);
        } else {
            ESP_LOGW(TAG, "no audio input: %s (synthetic sources still work)",
                     esp_err_to_name(input));
        }
    }
#endif

    if ((roles & OAL_ROLE_CONSUMER) != 0) {
        ESP_ERROR_CHECK(oal_stream_consumer_start(OAL_RTP_DEFAULT_PORT));

#if CONFIG_OAL_I2S_ENABLED
        /* Audio out is attached rather than built in, so a node with no
         * DAC still receives and still measures the link — which is how
         * every number in LINK-MEASUREMENTS.md was gathered. A failure
         * here is logged and survived for the same reason. */
        static oal_playout_config_t playout = {
            .bclk_gpio   = CONFIG_OAL_I2S_BCLK_GPIO,
            .ws_gpio     = CONFIG_OAL_I2S_WS_GPIO,
            .dout_gpio   = CONFIG_OAL_I2S_DOUT_GPIO,
            .sample_rate = OAL_RTP_SAMPLE_RATE,
            .target_ms   = CONFIG_OAL_PLAYOUT_MS,
        };
        /*
         * Plus this node's own trim, so a DAC can be held back to meet a
         * dongle that plays later through the same stream.
         *
         * Assigned rather than initialised, because `playout` is static
         * and a static initialiser has to be a constant expression -- the
         * same reason the three reads below are assignments. Written as an
         * initialiser first, which does not compile, and the file already
         * had the answer three lines further down.
         */
        playout.target_ms += oal_config_get_delay_ms();

        /* How much room the target has to move in, which is a different
         * question from where the target sits and is answered by an
         * allocation rather than a servo. Read here, at boot, because that
         * is the only moment the ring can be sized. */
        playout.ring_ms = oal_config_get_ring_ms();

        /* Read once at boot: /config stores a channel change and reports
         * that it applies at reboot, so this is where it takes effect. */
        playout.channel = oal_config_get_channel();

        /* Which output stage this board has (docs/USB-AUDIO.md). Read at
         * boot for the same reason as the channel: it describes the box,
         * not what is playing, and changing it means rewiring or unplugging
         * something anyway. */
        playout.output = oal_config_get_output();

        /* Volume is different: POST /volume applies it live and this only
         * restores what the room was left at. A speaker that came back at
         * full scale after an update would be a genuinely unpleasant
         * surprise at seven in the morning. */
        playout.volume = oal_config_get_volume();

        esp_err_t audio = oal_playout_start(&playout);
        if (audio == ESP_OK) {
            oal_stream_consumer_set_sink(oal_playout_submit);
        } else {
            ESP_LOGW(TAG, "no audio output: %s (still receiving and counting)",
                     esp_err_to_name(audio));
        }
#endif

        /* Listening is not the same as being known about. A Consumer finds
         * the Controller and says it is ready, which is what gets it added
         * to a stream — by asking, rather than by having been present when
         * the music started (decision 9). */
        ESP_ERROR_CHECK(oal_join_start(discovery.id, OAL_RTP_DEFAULT_PORT));
    }

    /*
     * Last, because everything above it is what "this image runs" means.
     */
    confirm_image_later();
}
