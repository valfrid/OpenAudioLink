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
#include "oal_discovery.h"
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
}
