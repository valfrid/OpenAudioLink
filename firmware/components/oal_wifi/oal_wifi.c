#include "oal_wifi.h"

#include <stdlib.h>
#include <string.h>

#include "esp_event.h"
#include "esp_http_server.h"
#include "esp_log.h"
#include "esp_mac.h"
#include "esp_netif.h"
#include "esp_system.h"
#include "esp_wifi.h"
#include "freertos/FreeRTOS.h"
#include "freertos/event_groups.h"
#include "freertos/task.h"
#include "nvs.h"

static const char *TAG = "oal_wifi";

#define NVS_NAMESPACE "oal"
#define MAX_STA_RETRIES 10

#define CONNECTED_BIT BIT0
#define FAILED_BIT BIT1

static EventGroupHandle_t s_events;
static int s_retries;

/* ---------- credentials ---------- */

static esp_err_t load_credentials(char *ssid, size_t ssid_len, char *password, size_t password_len)
{
    nvs_handle_t nvs;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READONLY, &nvs);
    if (err != ESP_OK) {
        ESP_LOGW(TAG, "no stored configuration (nvs_open: %s)", esp_err_to_name(err));
        return err;
    }

    err = nvs_get_str(nvs, "wifi_ssid", ssid, &ssid_len);
    if (err != ESP_OK) {
        ESP_LOGW(TAG, "no stored SSID (nvs_get_str: %s)", esp_err_to_name(err));
        nvs_close(nvs);
        return err;
    }

    err = nvs_get_str(nvs, "wifi_pass", password, &password_len);
    if (err != ESP_OK) {
        ESP_LOGW(TAG, "stored SSID but no password (nvs_get_str: %s)", esp_err_to_name(err));
        nvs_close(nvs);
        return err;
    }

    ESP_LOGI(TAG, "loaded stored credentials for \"%s\"", ssid);
    nvs_close(nvs);
    return ESP_OK;
}

esp_err_t oal_wifi_set_credentials(const char *ssid, const char *password)
{
    if (ssid == NULL || ssid[0] == '\0' || password == NULL) {
        return ESP_ERR_INVALID_ARG;
    }

    nvs_handle_t nvs;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READWRITE, &nvs);
    if (err != ESP_OK) {
        return err;
    }

    err = nvs_set_str(nvs, "wifi_ssid", ssid);
    if (err == ESP_OK) {
        err = nvs_set_str(nvs, "wifi_pass", password);
    }
    if (err == ESP_OK) {
        err = nvs_commit(nvs);
    }
    nvs_close(nvs);

    if (err != ESP_OK) {
        ESP_LOGE(TAG, "storing credentials failed: %s", esp_err_to_name(err));
    }
    return err;
}

/* ---------- station mode ---------- */

static void on_wifi_event(void *arg, esp_event_base_t base, int32_t event_id, void *data)
{
    if (base == WIFI_EVENT && event_id == WIFI_EVENT_STA_START) {
        esp_wifi_connect();
    } else if (base == WIFI_EVENT && event_id == WIFI_EVENT_STA_DISCONNECTED) {
        if (++s_retries > MAX_STA_RETRIES) {
            xEventGroupSetBits(s_events, FAILED_BIT);
        } else {
            ESP_LOGW(TAG, "disconnected, retry %d/%d", s_retries, MAX_STA_RETRIES);
            esp_wifi_connect();
        }
    } else if (base == IP_EVENT && event_id == IP_EVENT_STA_GOT_IP) {
        ip_event_got_ip_t *event = (ip_event_got_ip_t *)data;
        ESP_LOGI(TAG, "got ip " IPSTR, IP2STR(&event->ip_info.ip));
        s_retries = 0;
        xEventGroupSetBits(s_events, CONNECTED_BIT);
    }
}

static bool try_station(const char *ssid, const char *password)
{
    wifi_config_t config = { 0 };
    strlcpy((char *)config.sta.ssid, ssid, sizeof(config.sta.ssid));
    strlcpy((char *)config.sta.password, password, sizeof(config.sta.password));

    s_retries = 0;
    xEventGroupClearBits(s_events, CONNECTED_BIT | FAILED_BIT);

    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_STA));
    ESP_ERROR_CHECK(esp_wifi_set_config(WIFI_IF_STA, &config));
    ESP_ERROR_CHECK(esp_wifi_start());

    ESP_LOGI(TAG, "joining \"%s\"", ssid);
    EventBits_t bits = xEventGroupWaitBits(s_events, CONNECTED_BIT | FAILED_BIT,
                                           pdFALSE, pdFALSE, portMAX_DELAY);
    if (bits & CONNECTED_BIT) {
        return true;
    }

    ESP_LOGE(TAG, "could not join \"%s\"", ssid);
    ESP_ERROR_CHECK(esp_wifi_stop());
    return false;
}

/* ---------- provisioning portal ---------- */

static const char PORTAL_PAGE[] =
    "<!doctype html><html><head><meta charset=\"utf-8\">"
    "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
    "<title>OpenAudioLink setup</title>"
    "<style>body{font-family:sans-serif;max-width:22rem;margin:2rem auto;padding:0 1rem}"
    "input{width:100%;padding:.5rem;margin:.25rem 0 1rem;box-sizing:border-box}"
    "button{padding:.5rem 1.5rem}</style></head><body>"
    "<h2>OpenAudioLink setup</h2>"
    "<p>Enter the Wi-Fi network this device should join.</p>"
    "<form method=\"post\" action=\"/save\">"
    "<label>Network name (SSID)</label><input name=\"ssid\" required>"
    "<label>Password</label><input name=\"password\" type=\"password\">"
    "<button type=\"submit\">Save and reboot</button></form></body></html>";

static void url_decode(char *value)
{
    char *out = value;
    for (char *in = value; *in != '\0'; in++) {
        if (*in == '+') {
            *out++ = ' ';
        } else if (*in == '%' && in[1] != '\0' && in[2] != '\0') {
            char hex[3] = { in[1], in[2], '\0' };
            *out++ = (char)strtol(hex, NULL, 16);
            in += 2;
        } else {
            *out++ = *in;
        }
    }
    *out = '\0';
}

static esp_err_t portal_get_handler(httpd_req_t *req)
{
    return httpd_resp_send(req, PORTAL_PAGE, HTTPD_RESP_USE_STRLEN);
}

static esp_err_t portal_save_handler(httpd_req_t *req)
{
    char body[512];
    int received = 0;
    int remaining = req->content_len;

    ESP_LOGI(TAG, "save request, content length %d", remaining);

    if (remaining <= 0 || remaining >= (int)sizeof(body)) {
        ESP_LOGE(TAG, "unexpected body length %d", remaining);
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "bad body length");
        return ESP_FAIL;
    }

    /* A form body can arrive in more than one chunk, so read until the
     * declared content length is satisfied rather than assuming one read. */
    while (remaining > 0) {
        int len = httpd_req_recv(req, body + received, remaining);
        if (len == HTTPD_SOCK_ERR_TIMEOUT) {
            continue;
        }
        if (len <= 0) {
            ESP_LOGE(TAG, "receiving body failed after %d bytes", received);
            httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "truncated body");
            return ESP_FAIL;
        }
        received += len;
        remaining -= len;
    }
    body[received] = '\0';

    char ssid[33] = { 0 };
    char password[65] = { 0 };
    esp_err_t parsed = httpd_query_key_value(body, "ssid", ssid, sizeof(ssid));
    if (parsed != ESP_OK) {
        ESP_LOGE(TAG, "no ssid field in body (%s)", esp_err_to_name(parsed));
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "missing ssid");
        return ESP_FAIL;
    }
    httpd_query_key_value(body, "password", password, sizeof(password));
    url_decode(ssid);
    url_decode(password);

    if (oal_wifi_set_credentials(ssid, password) != ESP_OK) {
        httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "could not save");
        return ESP_FAIL;
    }

    ESP_LOGI(TAG, "credentials saved for \"%s\", rebooting", ssid);
    httpd_resp_send(req, "Saved. The device is rebooting and will join your network.",
                    HTTPD_RESP_USE_STRLEN);
    vTaskDelay(pdMS_TO_TICKS(1000));
    esp_restart();
    return ESP_OK;
}

/*
 * Each step is logged and checked individually rather than wrapped in
 * ESP_ERROR_CHECK: a device that cannot start its setup portal is
 * unrecoverable in the field, so it must say which call failed instead of
 * aborting silently.
 */
static void start_portal(void)
{
    esp_err_t err;

    uint8_t mac[6];
    err = esp_read_mac(mac, ESP_MAC_WIFI_SOFTAP);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "esp_read_mac failed: %s", esp_err_to_name(err));
        return;
    }

    wifi_config_t config = { 0 };
    snprintf((char *)config.ap.ssid, sizeof(config.ap.ssid), "OpenAudioLink-%02X%02X%02X",
             mac[3], mac[4], mac[5]);
    config.ap.ssid_len = strlen((char *)config.ap.ssid);
    config.ap.authmode = WIFI_AUTH_OPEN;
    config.ap.max_connection = 4;
    config.ap.channel = 1; /* explicit: 0 is not a valid channel on all IDF versions */

    ESP_LOGI(TAG, "starting setup access point \"%s\"", (char *)config.ap.ssid);

    err = esp_wifi_set_mode(WIFI_MODE_AP);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "esp_wifi_set_mode failed: %s", esp_err_to_name(err));
        return;
    }

    err = esp_wifi_set_config(WIFI_IF_AP, &config);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "esp_wifi_set_config failed: %s", esp_err_to_name(err));
        return;
    }

    err = esp_wifi_start();
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "esp_wifi_start failed: %s", esp_err_to_name(err));
        return;
    }
    ESP_LOGI(TAG, "access point started");

    httpd_handle_t server = NULL;
    httpd_config_t server_config = HTTPD_DEFAULT_CONFIG();
    err = httpd_start(&server, &server_config);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "httpd_start failed: %s", esp_err_to_name(err));
        return;
    }

    httpd_uri_t root = { .uri = "/", .method = HTTP_GET, .handler = portal_get_handler };
    httpd_uri_t save = { .uri = "/save", .method = HTTP_POST, .handler = portal_save_handler };
    httpd_register_uri_handler(server, &root);
    httpd_register_uri_handler(server, &save);

    ESP_LOGW(TAG, "provisioning portal up: connect to \"%s\" and open http://192.168.4.1/",
             (char *)config.ap.ssid);
}

/* ---------- entry point ---------- */

oal_wifi_result_t oal_wifi_start(const char *fallback_ssid, const char *fallback_password)
{
    s_events = xEventGroupCreate();

    ESP_ERROR_CHECK(esp_netif_init());
    ESP_ERROR_CHECK(esp_event_loop_create_default());
    esp_netif_create_default_wifi_sta();
    esp_netif_create_default_wifi_ap();

    wifi_init_config_t init_config = WIFI_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_wifi_init(&init_config));
    ESP_ERROR_CHECK(esp_event_handler_register(WIFI_EVENT, ESP_EVENT_ANY_ID, on_wifi_event, NULL));
    ESP_ERROR_CHECK(esp_event_handler_register(IP_EVENT, IP_EVENT_STA_GOT_IP, on_wifi_event, NULL));

    char ssid[33] = { 0 };
    char password[65] = { 0 };
    if (load_credentials(ssid, sizeof(ssid), password, sizeof(password)) != ESP_OK) {
        if (fallback_ssid != NULL && fallback_ssid[0] != '\0') {
            strlcpy(ssid, fallback_ssid, sizeof(ssid));
            strlcpy(password, fallback_password != NULL ? fallback_password : "", sizeof(password));
        }
    }

    if (ssid[0] != '\0' && try_station(ssid, password)) {
        return OAL_WIFI_STA;
    }

    start_portal();
    return OAL_WIFI_PORTAL;
}
