#include "oal_wifi.h"

#include "oal_config.h"

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

    /*
     * In a mesh every node advertises the same SSID under a different
     * BSSID, so joining "the network" means choosing between them. The
     * zeroed default is WIFI_FAST_SCAN, which takes the first node that
     * answers and never compares — the driver's remembered channel is
     * probed first, so a node that once landed on a distant mesh point
     * returns to it after every reboot however close a stronger one is.
     * sort_method has no effect in that mode; there is nothing to sort.
     *
     * Scan every channel and take the strongest instead. It costs a second
     * or so at boot, which buys a receiver the link margin it needs: a
     * stream is ~2.4 Mbit/s of unicast, and the difference between -55 and
     * -77 dBm is the difference between headroom and dropouts.
     */
    config.sta.scan_method = WIFI_ALL_CHANNEL_SCAN;
    config.sta.sort_method = WIFI_CONNECT_AP_BY_SIGNAL;
    /* Explicit: 0 here means "unset", and a literal 0 dBm floor would
     * reject every real access point. */
    config.sta.threshold.rssi = -127;

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

/*
 * Provisioning is the one moment the board is in your hands and you know
 * which one it is, so it asks for everything that distinguishes it: the
 * network, a name, and what it is for. Getting the role here saves a trip
 * through the Hub before the node has ever done anything.
 *
 * The password can be revealed. It is typed once, alone, in front of a
 * board — and a silent typo costs a failed join and a full re-provision,
 * which is a far worse outcome than the nobody standing behind you.
 *
 * This is a printf format string, not literal HTML: %s is the default node
 * name the caller fills in from the MAC, and every literal percent in the
 * CSS must therefore be doubled.
 */
static const char PORTAL_TEMPLATE[] =
    "<!doctype html><html><head><meta charset=\"utf-8\">"
    "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
    "<title>OpenAudioLink setup</title>"
    "<style>body{font-family:sans-serif;max-width:22rem;margin:2rem auto;padding:0 1rem}"
    "input[type=text],input[type=password]{width:100%%;padding:.5rem;margin:.25rem 0 1rem;"
    "box-sizing:border-box}"
    "label{display:block;margin-top:.5rem}"
    ".row{margin:.25rem 0 1rem}.row label{display:inline-block;margin:0 1rem 0 0;font-weight:normal}"
    ".show{font-size:.9rem;margin:-.75rem 0 1rem}"
    "button{padding:.5rem 1.5rem}</style></head><body>"
    "<h2>OpenAudioLink setup</h2>"
    "<form method=\"post\" action=\"/save\">"
    "<label>Network name (SSID)</label>"
    "<input name=\"ssid\" type=\"text\" autocapitalize=\"none\" autocorrect=\"off\" required>"
    "<label>Password</label>"
    "<input id=\"pw\" name=\"password\" type=\"password\" autocapitalize=\"none\" autocorrect=\"off\">"
    "<div class=\"show\"><label><input type=\"checkbox\" onclick=\"pw.type="
    "this.checked?'text':'password'\"> Show password</label></div>"
    "<label>Node name</label>"
    "<input name=\"name\" type=\"text\" value=\"%s\" maxlength=\"31\">"
    "<label>This node is a</label>"
    "<div class=\"row\">"
    "<label><input type=\"radio\" name=\"roles\" value=\"consumer\" checked> Consumer (plays)</label>"
    "<label><input type=\"radio\" name=\"roles\" value=\"producer\"> Producer (sends)</label>"
    "<label><input type=\"radio\" name=\"roles\" value=\"producer,consumer\"> Both</label>"
    "</div>"
    "<button type=\"submit\">Save and reboot</button></form></body></html>";

/* Built once at portal start, because the default name comes from the MAC.
 * The page renders to about 1.4 KB; the slack is so an edit to the form
 * does not silently become a portal that refuses to start. */
static char s_portal_page[2048];

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
    return httpd_resp_send(req, s_portal_page, HTTPD_RESP_USE_STRLEN);
}

/*
 * Applies the provisioning form from a urlencoded
 * "ssid=...&password=...&name=...&roles=..." string, whatever request
 * shape delivered it.
 *
 * Only the SSID is required. Name and roles are stored when present and
 * left at their defaults otherwise, so a form from an older page still
 * provisions a working node.
 */
static esp_err_t apply_credentials(httpd_req_t *req, char *form)
{
    char ssid[33] = { 0 };
    char password[65] = { 0 };
    char name[OAL_NAME_MAX] = { 0 };
    char roles[OAL_ROLES_STR_MAX] = { 0 };

    esp_err_t parsed = httpd_query_key_value(form, "ssid", ssid, sizeof(ssid));
    if (parsed != ESP_OK) {
        ESP_LOGE(TAG, "no ssid field in request (%s)", esp_err_to_name(parsed));
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "missing ssid");
        return ESP_FAIL;
    }
    httpd_query_key_value(form, "password", password, sizeof(password));
    url_decode(ssid);
    url_decode(password);

    /* Roles before credentials: a bad role name should fail while the node
     * is still reachable on its own access point, not after it has
     * rebooted onto a network with the wrong job. */
    if (httpd_query_key_value(form, "roles", roles, sizeof(roles)) == ESP_OK) {
        url_decode(roles);
        oal_roles_t parsed_roles = oal_roles_parse(roles);
        if (parsed_roles == OAL_ROLE_NONE) {
            ESP_LOGE(TAG, "unrecognised roles \"%s\"", roles);
            httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "unknown role");
            return ESP_FAIL;
        }
        if (oal_config_set_roles(parsed_roles) != ESP_OK) {
            httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "could not save roles");
            return ESP_FAIL;
        }
    }

    if (httpd_query_key_value(form, "name", name, sizeof(name)) == ESP_OK) {
        url_decode(name);
        if (oal_config_set_name(name) != ESP_OK) {
            ESP_LOGW(TAG, "could not store node name; keeping the default");
        }
    }

    if (oal_wifi_set_credentials(ssid, password) != ESP_OK) {
        httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "could not save");
        return ESP_FAIL;
    }

    ESP_LOGI(TAG, "provisioned: ssid \"%s\", name \"%s\", roles \"%s\"; rebooting",
             ssid, name[0] ? name : "(default)", roles[0] ? roles : "(default)");
    httpd_resp_send(req, "Saved. The device is rebooting and will join your network.",
                    HTTPD_RESP_USE_STRLEN);
    vTaskDelay(pdMS_TO_TICKS(1000));
    esp_restart();
    return ESP_OK;
}

static esp_err_t portal_save_post_handler(httpd_req_t *req)
{
    char body[512];
    int received = 0;
    int remaining = req->content_len;

    ESP_LOGI(TAG, "save (POST), content length %d", remaining);

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

    return apply_credentials(req, body);
}

/*
 * Some mobile browsers submit the form as GET, putting the fields in the
 * query string. Rejecting that with 405 leaves the user unable to
 * provision the device, so both shapes are accepted.
 */
static esp_err_t portal_save_get_handler(httpd_req_t *req)
{
    char query[512];

    esp_err_t err = httpd_req_get_url_query_str(req, query, sizeof(query));
    if (err != ESP_OK) {
        ESP_LOGW(TAG, "save (GET) without usable query (%s); showing the form again",
                 esp_err_to_name(err));
        return httpd_resp_send(req, PORTAL_PAGE, HTTPD_RESP_USE_STRLEN);
    }

    ESP_LOGI(TAG, "save (GET), query length %d", (int)strlen(query));
    return apply_credentials(req, query);
}

/* Cheap sanity value, and it costs nothing in plain AP mode. */
static void log_tx_power(void)
{
    int8_t power = 0;
    if (esp_wifi_get_max_tx_power(&power) == ESP_OK) {
        ESP_LOGI(TAG, "max tx power %d (%.2f dBm)", power, power / 4.0);
    }
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

    /* The station MAC, though this names an access point: it is the one
     * the device identity and node name are built from, and the whole
     * point of the suffix is that the network you provision through and
     * the node that then appears are recognisably the same board. The
     * SoftAP MAC differs from it by one in the last byte, which would
     * make the two labels disagree by exactly enough to mislead. */
    uint8_t mac[6];
    err = esp_read_mac(mac, ESP_MAC_WIFI_STA);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "esp_read_mac failed: %s", esp_err_to_name(err));
        return;
    }

    /* The default name offered by the form is the one the node would use
     * anyway, so leaving the field alone is the same as not filling it in.
     * A stored name wins, which makes the portal show what the node is
     * currently called rather than what it was called when new. */
    char default_name[OAL_NAME_MAX];
    if (oal_config_get_name(default_name, sizeof(default_name)) != ESP_OK) {
        snprintf(default_name, sizeof(default_name), "%s-%02X%02X%02X",
                 CONFIG_OAL_NODE_NAME, mac[3], mac[4], mac[5]);
    }
    if (snprintf(s_portal_page, sizeof(s_portal_page), PORTAL_TEMPLATE, default_name)
            >= (int)sizeof(s_portal_page)) {
        ESP_LOGE(TAG, "portal page does not fit its buffer");
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

    /* Plain AP, not AP+STA. With the station interface also up, clients
     * associate but the access point's DHCP server never hands out a
     * lease, so a phone joins and then gives up with no address. */
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

    log_tx_power();

    httpd_handle_t server = NULL;
    httpd_config_t server_config = HTTPD_DEFAULT_CONFIG();
    err = httpd_start(&server, &server_config);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "httpd_start failed: %s", esp_err_to_name(err));
        return;
    }

    httpd_uri_t root = { .uri = "/", .method = HTTP_GET, .handler = portal_get_handler };
    httpd_uri_t save_post = { .uri = "/save", .method = HTTP_POST, .handler = portal_save_post_handler };
    httpd_uri_t save_get = { .uri = "/save", .method = HTTP_GET, .handler = portal_save_get_handler };
    httpd_register_uri_handler(server, &root);
    httpd_register_uri_handler(server, &save_post);
    httpd_register_uri_handler(server, &save_get);

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
