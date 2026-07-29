#include "oal_control.h"

#include <stdlib.h>
#include <string.h>

#include "cJSON.h"
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

static esp_err_t status_handler(httpd_req_t *req)
{
    int rssi = 0;
    wifi_ap_record_t ap;
    if (esp_wifi_sta_get_ap_info(&ap) == ESP_OK) {
        rssi = ap.rssi;
    }

    char roles[OAL_ROLES_STR_MAX];
    if (oal_roles_to_json(s_config.roles, roles, sizeof(roles)) < 0) {
        httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "roles too large");
        return ESP_FAIL;
    }

    char body[512];
    int len = snprintf(body, sizeof(body),
                       "{\"oal\":\"" PROTOCOL_VERSION "\",\"id\":\"%s\",\"name\":\"%s\","
                       "\"roles\":%s,\"hw\":\"%s\",\"fw\":\"%s\","
                       "\"uptimeS\":%lld,\"wifi\":{\"rssi\":%d},"
                       "\"audio\":{\"state\":\"idle\"}}",
                       s_config.id, s_config.name, roles,
                       s_config.hardware_profile, s_config.firmware_version,
                       (long long)(esp_timer_get_time() / 1000000), rssi);
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

    httpd_handle_t server = NULL;
    esp_err_t err = httpd_start(&server, &server_config);
    if (err != ESP_OK) {
        return err;
    }

    httpd_uri_t status = { .uri = "/status", .method = HTTP_GET, .handler = status_handler };
    httpd_uri_t reboot = { .uri = "/reboot", .method = HTTP_POST, .handler = reboot_handler };
    httpd_uri_t ota = { .uri = "/ota", .method = HTTP_POST, .handler = ota_handler };
    httpd_uri_t set_config = { .uri = "/config", .method = HTTP_POST, .handler = config_handler };
    httpd_register_uri_handler(server, &status);
    httpd_register_uri_handler(server, &reboot);
    httpd_register_uri_handler(server, &ota);
    httpd_register_uri_handler(server, &set_config);

    ESP_LOGI(TAG, "control server on port %d", CONTROL_PORT);
    return ESP_OK;
}
