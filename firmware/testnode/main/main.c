/*
 * OpenAudioLink test node (Phase 2.3).
 *
 * Proves the first discovery milestone:
 *   ESP boots -> announces -> Hub discovers -> device appears in UI
 *
 * Wi-Fi credentials are build-time configuration (menuconfig) until USB
 * provisioning lands in Phase 2.5. Runs on ESP32-C3 (temporary development
 * hardware) and ESP32-S3 (reference platform); it contains no
 * hardware-profile-specific code.
 */

#include <string.h>

#include "esp_event.h"
#include "esp_log.h"
#include "esp_mac.h"
#include "esp_netif.h"
#include "esp_wifi.h"
#include "freertos/FreeRTOS.h"
#include "freertos/event_groups.h"
#include "nvs_flash.h"
#include "sdkconfig.h"

#include "oal_discovery.h"

static const char *TAG = "oal_testnode";

#define FIRMWARE_VERSION "0.1.0"

#if CONFIG_IDF_TARGET_ESP32S3
#define HARDWARE_PROFILE "esp32s3-devkit"
#elif CONFIG_IDF_TARGET_ESP32C3
#define HARDWARE_PROFILE "esp32c3-devkit"
#else
#define HARDWARE_PROFILE CONFIG_IDF_TARGET "-devkit"
#endif

#define WIFI_CONNECTED_BIT BIT0

static EventGroupHandle_t s_wifi_events;

static void on_wifi_event(void *arg, esp_event_base_t base, int32_t event_id, void *data)
{
    if (base == WIFI_EVENT && event_id == WIFI_EVENT_STA_START) {
        esp_wifi_connect();
    } else if (base == WIFI_EVENT && event_id == WIFI_EVENT_STA_DISCONNECTED) {
        xEventGroupClearBits(s_wifi_events, WIFI_CONNECTED_BIT);
        ESP_LOGW(TAG, "disconnected, retrying");
        vTaskDelay(pdMS_TO_TICKS(1000));
        esp_wifi_connect();
    } else if (base == IP_EVENT && event_id == IP_EVENT_STA_GOT_IP) {
        ip_event_got_ip_t *event = (ip_event_got_ip_t *)data;
        ESP_LOGI(TAG, "got ip " IPSTR, IP2STR(&event->ip_info.ip));
        xEventGroupSetBits(s_wifi_events, WIFI_CONNECTED_BIT);
    }
}

static void wifi_connect(void)
{
    s_wifi_events = xEventGroupCreate();

    ESP_ERROR_CHECK(esp_netif_init());
    ESP_ERROR_CHECK(esp_event_loop_create_default());
    esp_netif_create_default_wifi_sta();

    wifi_init_config_t init_config = WIFI_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_wifi_init(&init_config));

    ESP_ERROR_CHECK(esp_event_handler_register(WIFI_EVENT, ESP_EVENT_ANY_ID, on_wifi_event, NULL));
    ESP_ERROR_CHECK(esp_event_handler_register(IP_EVENT, IP_EVENT_STA_GOT_IP, on_wifi_event, NULL));

    wifi_config_t wifi_config = { 0 };
    strlcpy((char *)wifi_config.sta.ssid, CONFIG_OAL_WIFI_SSID, sizeof(wifi_config.sta.ssid));
    strlcpy((char *)wifi_config.sta.password, CONFIG_OAL_WIFI_PASSWORD,
            sizeof(wifi_config.sta.password));

    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_STA));
    ESP_ERROR_CHECK(esp_wifi_set_config(WIFI_IF_STA, &wifi_config));
    ESP_ERROR_CHECK(esp_wifi_start());

    xEventGroupWaitBits(s_wifi_events, WIFI_CONNECTED_BIT, pdFALSE, pdTRUE, portMAX_DELAY);
}

void app_main(void)
{
    esp_err_t err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        err = nvs_flash_init();
    }
    ESP_ERROR_CHECK(err);

    ESP_LOGI(TAG, "OpenAudioLink test node %s (%s)", FIRMWARE_VERSION, HARDWARE_PROFILE);

    if (strlen(CONFIG_OAL_WIFI_SSID) == 0) {
        ESP_LOGW(TAG, "no Wi-Fi SSID configured; set one via menuconfig "
                      "(OpenAudioLink Test Node). Idling.");
        return;
    }

    wifi_connect();

    /* Factory identity from the Wi-Fi MAC; see protocol/IDENTITY.md.
     * Provisioned identities arrive with USB provisioning in Phase 2.5. */
    uint8_t mac[6];
    ESP_ERROR_CHECK(esp_read_mac(mac, ESP_MAC_WIFI_STA));

    oal_discovery_config_t config = {
        .role = "receiver",
        .hardware_profile = HARDWARE_PROFILE,
        .firmware_version = FIRMWARE_VERSION,
    };
    snprintf(config.id, sizeof(config.id), "mac-%02x%02x%02x%02x%02x%02x",
             mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
    strlcpy(config.name, CONFIG_OAL_NODE_NAME, sizeof(config.name));

    ESP_ERROR_CHECK(oal_discovery_start(&config));
}
