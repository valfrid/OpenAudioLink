#include "oal_control.h"

#include <stdlib.h>
#include <string.h>

#include "cJSON.h"
#include "oal_stream.h"
#include "esp_http_server.h"
#include "esp_https_ota.h"
#include "esp_log.h"
#include "esp_system.h"
#include "esp_timer.h"
#include "esp_wifi.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

static const char *TAG = "oal_control";

#define CONTROL_PORT 41001
#define PROTOCOL_VERSION "0.1"

static oal_control_config_t s_config;

/* ---------- GET /status ---------- */

/*
 * An SSID is arbitrary bytes, not a safe JSON string: a quote or backslash
 * in a network name would produce a document the Hub cannot parse, and the
 * node would look offline for a reason nothing reports.
 */
static void json_escape(const char *in, char *out, size_t out_size)
{
    size_t w = 0;
    for (size_t r = 0; in[r] != '\0' && w + 2 < out_size; r++) {
        unsigned char c = (unsigned char)in[r];
        if (c == '"' || c == '\\') {
            out[w++] = '\\';
            out[w++] = (char)c;
        } else if (c < 0x20) {
            if (w + 6 >= out_size) {
                break;
            }
            w += (size_t)snprintf(out + w, out_size - w, "\\u%04x", c);
        } else {
            out[w++] = (char)c;
        }
    }
    out[w] = '\0';
}

/*
 * The whole Wi-Fi picture, not just the signal: in a mesh every node
 * advertises the same SSID, so a weak RSSI on its own cannot distinguish
 * "far from the right access point" from "attached to the wrong one".
 * The BSSID is what answers that, and it is the reason this endpoint
 * exists rather than the announce carrying it — telemetry that changes
 * every few seconds does not belong in a multicast every device hears.
 */
static int format_wifi(char *out, size_t out_size)
{
    wifi_ap_record_t ap;
    if (esp_wifi_sta_get_ap_info(&ap) != ESP_OK) {
        return snprintf(out, out_size, "{\"joined\":false}");
    }

    char ssid[sizeof(ap.ssid) * 2 + 1];
    json_escape((const char *)ap.ssid, ssid, sizeof(ssid));

    return snprintf(out, out_size,
                    "{\"joined\":true,\"ssid\":\"%s\","
                    "\"bssid\":\"%02x:%02x:%02x:%02x:%02x:%02x\","
                    "\"channel\":%d,\"rssi\":%d}",
                    ssid, ap.bssid[0], ap.bssid[1], ap.bssid[2],
                    ap.bssid[3], ap.bssid[4], ap.bssid[5],
                    (int)ap.primary, ap.rssi);
}

static esp_err_t status_handler(httpd_req_t *req)
{
    char roles[OAL_ROLES_STR_MAX];
    if (oal_roles_to_json(s_config.roles, roles, sizeof(roles)) < 0) {
        httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "roles too large");
        return ESP_FAIL;
    }

    char wifi[192];
    int wifi_len = format_wifi(wifi, sizeof(wifi));
    if (wifi_len <= 0 || wifi_len >= (int)sizeof(wifi)) {
        httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "wifi status too large");
        return ESP_FAIL;
    }

    char body[640];
    int len = snprintf(body, sizeof(body),
                       "{\"oal\":\"" PROTOCOL_VERSION "\",\"id\":\"%s\",\"name\":\"%s\","
                       "\"roles\":%s,\"hw\":\"%s\",\"fw\":\"%s\","
                       "\"uptimeS\":%lld,\"heapFree\":%u,\"wifi\":%s,"
                       "\"audio\":{\"state\":\"idle\"}}",
                       s_config.id, s_config.name, roles,
                       s_config.hardware_profile, s_config.firmware_version,
                       (long long)(esp_timer_get_time() / 1000000),
                       (unsigned)esp_get_free_heap_size(), wifi);
    if (len <= 0 || len >= (int)sizeof(body)) {
        httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "status too large");
        return ESP_FAIL;
    }

    httpd_resp_set_type(req, "application/json");
    return httpd_resp_send(req, body, len);
}

/* ---------- POST /config ---------- */

/*
 * Sets the roles this node takes, as an array: {"roles":["consumer"]}.
 * Stored in NVS and applied at the next boot, because roles decide which
 * tasks start — switching a running node between producer and consumer
 * would mean tearing down live audio, and a reboot is both simpler and
 * more honest about what happened.
 */
static esp_err_t config_handler(httpd_req_t *req)
{
    char body[256];
    int len = httpd_req_recv(req, body, sizeof(body) - 1);
    if (len <= 0) {
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "empty body");
        return ESP_FAIL;
    }
    body[len] = '\0';

    cJSON *root = cJSON_ParseWithLength(body, len);
    const cJSON *array = root != NULL ? cJSON_GetObjectItemCaseSensitive(root, "roles") : NULL;
    if (!cJSON_IsArray(array)) {
        cJSON_Delete(root);
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "missing roles array");
        return ESP_FAIL;
    }

    oal_roles_t roles = OAL_ROLE_NONE;
    const cJSON *element = NULL;
    cJSON_ArrayForEach(element, array) {
        oal_role_t role = cJSON_IsString(element)
            ? oal_role_from_name(element->valuestring) : OAL_ROLE_NONE;
        if (role == OAL_ROLE_NONE) {
            cJSON_Delete(root);
            httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "unknown role");
            return ESP_FAIL;
        }
        roles |= role;
    }
    cJSON_Delete(root);

    if (oal_config_set_roles(roles) != ESP_OK) {
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "roles not stored");
        return ESP_FAIL;
    }

    char stored[OAL_ROLES_STR_MAX];
    char response[128];
    oal_roles_to_json(roles, stored, sizeof(stored));
    int n = snprintf(response, sizeof(response),
                     "{\"status\":\"stored\",\"roles\":%s,\"appliesAt\":\"reboot\"}", stored);
    httpd_resp_set_type(req, "application/json");
    return httpd_resp_send(req, response, n);
}

/* ---------- stream: GET /stream, POST /stream/start, POST /stream/stop ---------- */

/*
 * The measurement surface. A producer reports what it sent and whether its
 * pacing slipped; a consumer reports what arrived and how. Both are needed
 * to read a result: loss at the consumer means nothing without knowing the
 * producer actually kept its rate.
 */
static esp_err_t stream_get_handler(httpd_req_t *req)
{
    char body[640];
    int len;

    if ((s_config.roles & OAL_ROLE_PRODUCER) != 0) {
        oal_stream_producer_state_t p;
        oal_stream_producer_get(&p);
        len = snprintf(body, sizeof(body),
                       "{\"role\":\"producer\",\"running\":%s,\"port\":%u,"
                       "\"destinations\":%u,\"source\":\"%s\","
                       "\"packetsSent\":%u,\"datagramsSent\":%u,"
                       "\"sendErrors\":%u,\"latePackets\":%u}",
                       p.running ? "true" : "false", p.port,
                       (unsigned)p.destination_count,
                       p.source == OAL_RTP_SOURCE_TONE ? "tone" : "pattern",
                       (unsigned)p.packets_sent, (unsigned)p.datagrams_sent,
                       (unsigned)p.send_errors, (unsigned)p.late_packets);
    } else {
        oal_stream_consumer_state_t c;
        oal_stream_consumer_get(&c);
        char stats[OAL_RTP_STATS_JSON_MAX];
        if (oal_rtp_stats_to_json(&c.stats, stats, sizeof(stats)) < 0) {
            httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "stats too large");
            return ESP_FAIL;
        }
        len = snprintf(body, sizeof(body),
                       "{\"role\":\"consumer\",\"listening\":%s,\"port\":%u,"
                       "\"payloadErrors\":%u,\"foreignPackets\":%u,"
                       "\"lastSsrc\":\"%08x\",\"stats\":%s}",
                       c.listening ? "true" : "false", c.port,
                       (unsigned)c.payload_errors, (unsigned)c.foreign_packets,
                       (unsigned)c.last_ssrc, stats);
    }

    if (len <= 0 || len >= (int)sizeof(body)) {
        httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "stream status too large");
        return ESP_FAIL;
    }
    httpd_resp_set_type(req, "application/json");
    return httpd_resp_send(req, body, len);
}

static esp_err_t stream_start_handler(httpd_req_t *req)
{
    if ((s_config.roles & OAL_ROLE_PRODUCER) == 0) {
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "not a producer");
        return ESP_FAIL;
    }

    char body[512];
    int len = httpd_req_recv(req, body, sizeof(body) - 1);
    if (len <= 0) {
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "empty body");
        return ESP_FAIL;
    }
    body[len] = '\0';

    cJSON *root = cJSON_ParseWithLength(body, len);
    const cJSON *list = root != NULL
        ? cJSON_GetObjectItemCaseSensitive(root, "destinations") : NULL;
    if (!cJSON_IsArray(list) || cJSON_GetArraySize(list) == 0) {
        cJSON_Delete(root);
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "missing destinations");
        return ESP_FAIL;
    }

    oal_stream_request_t request = { 0 };
    const cJSON *element = NULL;
    cJSON_ArrayForEach(element, list) {
        if (!cJSON_IsString(element)
            || request.destination_count >= OAL_STREAM_MAX_DESTINATIONS) {
            cJSON_Delete(root);
            httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "bad destinations");
            return ESP_FAIL;
        }
        strlcpy(request.destinations[request.destination_count],
                element->valuestring,
                sizeof(request.destinations[0]));
        request.destination_count++;
    }

    const cJSON *port = cJSON_GetObjectItemCaseSensitive(root, "port");
    const cJSON *source = cJSON_GetObjectItemCaseSensitive(root, "source");
    const cJSON *tone = cJSON_GetObjectItemCaseSensitive(root, "toneHz");
    request.port = cJSON_IsNumber(port) ? (uint16_t)port->valueint : OAL_RTP_DEFAULT_PORT;
    request.source = (cJSON_IsString(source) && strcmp(source->valuestring, "tone") == 0)
        ? OAL_RTP_SOURCE_TONE : OAL_RTP_SOURCE_PATTERN;
    request.tone_hz = cJSON_IsNumber(tone) ? (uint32_t)tone->valueint : 1000;
    cJSON_Delete(root);

    if (oal_stream_producer_start(&request) != ESP_OK) {
        httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "could not start");
        return ESP_FAIL;
    }

    httpd_resp_set_type(req, "application/json");
    return httpd_resp_send(req, "{\"status\":\"streaming\"}", HTTPD_RESP_USE_STRLEN);
}

static esp_err_t stream_stop_handler(httpd_req_t *req)
{
    /* A consumer has nothing to stop but its counters, and clearing them is
     * how a measurement run begins. */
    if ((s_config.roles & OAL_ROLE_PRODUCER) != 0) {
        oal_stream_producer_stop();
    }
    if ((s_config.roles & OAL_ROLE_CONSUMER) != 0) {
        oal_stream_consumer_reset();
    }
    httpd_resp_set_type(req, "application/json");
    return httpd_resp_send(req, "{\"status\":\"stopped\"}", HTTPD_RESP_USE_STRLEN);
}

/* ---------- POST /reboot ---------- */

static void restart_task(void *arg)
{
    vTaskDelay(pdMS_TO_TICKS(500));
    esp_restart();
}

static esp_err_t reboot_handler(httpd_req_t *req)
{
    ESP_LOGW(TAG, "reboot requested");
    httpd_resp_set_type(req, "application/json");
    httpd_resp_send(req, "{\"status\":\"rebooting\"}", HTTPD_RESP_USE_STRLEN);
    xTaskCreate(restart_task, "oal_restart", 2048, NULL, 5, NULL);
    return ESP_OK;
}

/* ---------- POST /ota ---------- */

static void ota_task(void *arg)
{
    char *url = (char *)arg;
    ESP_LOGW(TAG, "starting OTA from %s", url);

    esp_http_client_config_t http_config = {
        .url = url,
        .timeout_ms = 30000,
        .keep_alive_enable = true,
    };
    esp_https_ota_config_t ota_config = {
        .http_config = &http_config,
    };

    esp_err_t err = esp_https_ota(&ota_config);
    if (err == ESP_OK) {
        ESP_LOGW(TAG, "OTA complete, rebooting into new firmware");
        vTaskDelay(pdMS_TO_TICKS(500));
        esp_restart();
    }

    ESP_LOGE(TAG, "OTA failed: %s", esp_err_to_name(err));
    free(url);
    vTaskDelete(NULL);
}

static esp_err_t ota_handler(httpd_req_t *req)
{
    char body[512];
    int len = httpd_req_recv(req, body, sizeof(body) - 1);
    if (len <= 0) {
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "empty body");
        return ESP_FAIL;
    }
    body[len] = '\0';

    cJSON *root = cJSON_ParseWithLength(body, len);
    const cJSON *url = root != NULL ? cJSON_GetObjectItemCaseSensitive(root, "url") : NULL;
    if (!cJSON_IsString(url) || strncmp(url->valuestring, "http", 4) != 0) {
        cJSON_Delete(root);
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "missing url");
        return ESP_FAIL;
    }

    char *url_copy = strdup(url->valuestring);
    cJSON_Delete(root);
    if (url_copy == NULL) {
        httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "out of memory");
        return ESP_FAIL;
    }

    if (xTaskCreate(ota_task, "oal_ota", 8192, url_copy, 5, NULL) != pdPASS) {
        free(url_copy);
        httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "could not start OTA");
        return ESP_FAIL;
    }

    httpd_resp_set_type(req, "application/json");
    return httpd_resp_send(req, "{\"status\":\"accepted\"}", HTTPD_RESP_USE_STRLEN);
}

/* ---------- startup ---------- */

esp_err_t oal_control_start(const oal_control_config_t *config)
{
    if (config == NULL || config->id == NULL || config->name == NULL
        || config->roles == OAL_ROLE_NONE
        || config->hardware_profile == NULL || config->firmware_version == NULL) {
        return ESP_ERR_INVALID_ARG;
    }
    s_config = *config;

    httpd_config_t server_config = HTTPD_DEFAULT_CONFIG();
    server_config.server_port = CONTROL_PORT;
    server_config.max_uri_handlers = 8; /* seven registered, and the default is eight */

    httpd_handle_t server = NULL;
    esp_err_t err = httpd_start(&server, &server_config);
    if (err != ESP_OK) {
        return err;
    }

    httpd_uri_t status = { .uri = "/status", .method = HTTP_GET, .handler = status_handler };
    httpd_uri_t reboot = { .uri = "/reboot", .method = HTTP_POST, .handler = reboot_handler };
    httpd_uri_t ota = { .uri = "/ota", .method = HTTP_POST, .handler = ota_handler };
    httpd_uri_t set_config = { .uri = "/config", .method = HTTP_POST, .handler = config_handler };
    httpd_uri_t stream = { .uri = "/stream", .method = HTTP_GET, .handler = stream_get_handler };
    httpd_uri_t stream_start =
        { .uri = "/stream/start", .method = HTTP_POST, .handler = stream_start_handler };
    httpd_uri_t stream_stop =
        { .uri = "/stream/stop", .method = HTTP_POST, .handler = stream_stop_handler };
    httpd_register_uri_handler(server, &status);
    httpd_register_uri_handler(server, &reboot);
    httpd_register_uri_handler(server, &ota);
    httpd_register_uri_handler(server, &set_config);
    httpd_register_uri_handler(server, &stream);
    httpd_register_uri_handler(server, &stream_start);
    httpd_register_uri_handler(server, &stream_stop);

    ESP_LOGI(TAG, "control server on port %d", CONTROL_PORT);
    return ESP_OK;
}
