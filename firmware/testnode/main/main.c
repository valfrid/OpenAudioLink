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
#include "freertos/FreeRTOS.h"
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

void app_main(void)
{
    esp_err_t err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        err = nvs_flash_init();
    }
    ESP_ERROR_CHECK(err);

    ESP_LOGI(TAG, "OpenAudioLink test node %s (%s)", FIRMWARE_VERSION, HARDWARE_PROFILE);

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
