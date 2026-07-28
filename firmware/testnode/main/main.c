/*
 * OpenAudioLink test node.
 *
 * "Hello world" milestone: boot -> Wi-Fi (NVS credentials or setup portal)
 * -> discovery announce -> control server -> OTA-updatable over the Hub.
 *
 * Runs on ESP32-C3 (temporary development hardware) and ESP32-S3
 * (reference platform); it contains no hardware-profile-specific code.
 */

#include <string.h>

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

#include "oal_control.h"
#include "oal_discovery.h"
#include "oal_wifi.h"

static const char *TAG = "oal_testnode";

#define FIRMWARE_VERSION "0.2.0"

#if CONFIG_IDF_TARGET_ESP32S3
#define HARDWARE_PROFILE "esp32s3-devkit"
#elif CONFIG_IDF_TARGET_ESP32C3
#define HARDWARE_PROFILE "esp32c3-devkit"
#else
#define HARDWARE_PROFILE CONFIG_IDF_TARGET "-devkit"
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
        char detail[128] = "";
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
                snprintf(detail, sizeof(detail), " | joined \"%s\" rssi %d, ip " IPSTR,
                         (char *)ap.ssid, ap.rssi, IP2STR(&ip.ip));
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

    static oal_discovery_config_t discovery = {
        .role = "receiver",
        .hardware_profile = HARDWARE_PROFILE,
        .firmware_version = FIRMWARE_VERSION,
    };
    snprintf(discovery.id, sizeof(discovery.id), "mac-%02x%02x%02x%02x%02x%02x",
             mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
    strlcpy(discovery.name, CONFIG_OAL_NODE_NAME, sizeof(discovery.name));

    static oal_control_config_t control;
    control.id = discovery.id;
    control.name = discovery.name;
    control.role = discovery.role;
    control.hardware_profile = discovery.hardware_profile;
    control.firmware_version = discovery.firmware_version;

    ESP_ERROR_CHECK(oal_control_start(&control));
    ESP_ERROR_CHECK(oal_discovery_start(&discovery));
}
