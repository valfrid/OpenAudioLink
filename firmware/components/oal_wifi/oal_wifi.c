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
#include "esp_timer.h"
#include "esp_wifi.h"
#include "freertos/FreeRTOS.h"
#include "freertos/event_groups.h"
#include "freertos/task.h"
#include "nvs.h"

static const char *TAG = "oal_wifi";

#define NVS_NAMESPACE "oal"

/*
 * How many joins may fail before the *first* connection is called off and
 * the provisioning portal opens. Only ever applies at boot: a node that has
 * been on the network and fell off has demonstrably correct credentials, so
 * giving up on it would be answering a radio problem with a password
 * question.
 */
#define MAX_STA_RETRIES 10

#define CONNECTED_BIT BIT0
#define FAILED_BIT BIT1

/*
 * Staying put, and coming back.
 *
 * Two faults met on hardware, both of which stop the music after some
 * minutes and neither of which looks like a radio problem from the outside.
 *
 * **The node used to give up.** After ten consecutive disconnects it set
 * FAILED_BIT and stopped calling esp_wifi_connect entirely — nothing was
 * waiting on that bit once boot was over, so the node simply left the
 * network until somebody power-cycled it. Ten disconnects is a normal
 * afternoon in a mesh. A node that has ever held an address now retries
 * forever, slowing down rather than stopping.
 *
 * **And it re-chose its access point every time.** The join config asks for
 * an all-channel scan sorted by signal, which is right for a first join and
 * wrong for every one after it: with two mesh radios at similar strength
 * the winner is a coin flip, so a node landed on a different access point
 * every second reconnect. Measured as a producer and its consumer drifting
 * apart onto different radios mid-record, which turns a two-hop path into a
 * three-hop one across the backhaul.
 *
 * So: remember where we were and ask for exactly that BSSID first. This is
 * the hysteresis, expressed as "do not even look elsewhere unless the
 * access point we were on will not have us" rather than as an RSSI margin —
 * a threshold has to be tuned against a house, and this does not. It is
 * also much faster, because a directed join skips the scan that costs a
 * second or more of silence.
 *
 * Only after several refusals does it fall back to scanning, which is the
 * case that matters when an access point has genuinely gone away.
 */
#define STICKY_ATTEMPTS 4

/* Backoff once a node has been trying for a while, so a radio that is
 * really gone does not spin the transmitter flat out. */
#define RECONNECT_DELAY_US 2000000
#define BACKOFF_AFTER_RETRIES 6

static EventGroupHandle_t s_events;
static int s_retries;

/** The access point this node last held an address on. */
static uint8_t s_last_bssid[6];
static bool s_have_last_bssid;

/** Consecutive directed joins tried since the last success. */
static int s_sticky_attempts;

/** True once this node has ever been on the network. */
static bool s_was_connected;

/** Counted and reported, because a roam is invisible otherwise. */
static uint32_t s_roams;

static esp_timer_handle_t s_reconnect_timer;

/* ---------- credentials ---------- */

/*
 * One reader for two networks.
 *
 * A node holds up to two sets of credentials: the network it was
 * provisioned onto, and the party network of decision 4's standalone mode
 * — the same SSID and passphrase on every node in a group, so that
 * equipment carried to a venue finds itself without anybody configuring
 * anything on the day.
 *
 * Only the *producer* is told it is hosting a party. A consumer never
 * needs to know: "try home, then try the other one" is the same rule in
 * the living room and at the venue, so it holds no mode and needs no
 * preparation before an event or cleaning up after one.
 */
static esp_err_t load_pair(const char *ssid_key, const char *pass_key,
                          char *ssid, size_t ssid_len,
                          char *password, size_t password_len)
{
    nvs_handle_t nvs;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READONLY, &nvs);
    if (err != ESP_OK) {
        ESP_LOGW(TAG, "no stored configuration (nvs_open: %s)", esp_err_to_name(err));
        return err;
    }

    err = nvs_get_str(nvs, ssid_key, ssid, &ssid_len);
    if (err != ESP_OK) {
        nvs_close(nvs);
        return err;
    }

    err = nvs_get_str(nvs, pass_key, password, &password_len);
    if (err != ESP_OK) {
        ESP_LOGW(TAG, "%s is set but %s is not", ssid_key, pass_key);
        nvs_close(nvs);
        return err;
    }

    nvs_close(nvs);
    return ESP_OK;
}

static esp_err_t store_pair(const char *ssid_key, const char *pass_key,
                           const char *ssid, const char *password)
{
    nvs_handle_t nvs;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READWRITE, &nvs);
    if (err != ESP_OK) {
        return err;
    }

    /* An empty SSID means forget this network, which is the only way to
     * take a party credential back off a node that has left the group. */
    if (ssid[0] == '\0') {
        nvs_erase_key(nvs, ssid_key);
        nvs_erase_key(nvs, pass_key);
    } else {
        err = nvs_set_str(nvs, ssid_key, ssid);
        if (err == ESP_OK) {
            err = nvs_set_str(nvs, pass_key, password);
        }
    }
    if (err == ESP_OK) {
        err = nvs_commit(nvs);
    }
    nvs_close(nvs);
    return err;
}

esp_err_t oal_wifi_set_credentials(const char *ssid, const char *password)
{
    if (ssid == NULL || ssid[0] == '\0' || password == NULL) {
        return ESP_ERR_INVALID_ARG;
    }
    esp_err_t err = store_pair("wifi_ssid", "wifi_pass", ssid, password);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "storing credentials failed: %s", esp_err_to_name(err));
    }
    return err;
}

esp_err_t oal_wifi_set_party(const char *ssid, const char *password)
{
    if (ssid == NULL || password == NULL) {
        return ESP_ERR_INVALID_ARG;
    }
    esp_err_t err = store_pair("party_ssid", "party_pass", ssid, password);
    if (err == ESP_OK) {
        ESP_LOGW(TAG, ssid[0] == '\0' ? "party network forgotten"
                                      : "party network stored; it applies at the next boot");
    } else {
        ESP_LOGE(TAG, "storing the party network failed: %s", esp_err_to_name(err));
    }
    return err;
}

bool oal_wifi_has_party(void)
{
    char ssid[33] = { 0 };
    char password[65] = { 0 };
    return load_pair("party_ssid", "party_pass",
                     ssid, sizeof(ssid), password, sizeof(password)) == ESP_OK
           && ssid[0] != '\0';
}

/* ---------- station mode ---------- */

/**
 * Asks for the access point we were last on, or for the best one going.
 *
 * Directed joins skip the scan entirely, which is both faster and the
 * whole point: a scan is what lets a node change its mind about where it
 * lives.
 */
static void request_join(void)
{
    wifi_config_t config = { 0 };
    if (esp_wifi_get_config(WIFI_IF_STA, &config) != ESP_OK) {
        esp_wifi_connect();
        return;
    }

    bool sticky = s_have_last_bssid && s_sticky_attempts < STICKY_ATTEMPTS;
    if (sticky) {
        memcpy(config.sta.bssid, s_last_bssid, sizeof(config.sta.bssid));
        config.sta.bssid_set = true;
        s_sticky_attempts++;
    }
    else {
        /* Given up on where we were. Scan and take the strongest, which is
         * the boot behaviour and the right answer when an access point has
         * actually gone away. */
        config.sta.bssid_set = false;
        if (s_have_last_bssid) {
            ESP_LOGW(TAG, "%d directed joins refused; scanning for any access point",
                     STICKY_ATTEMPTS);
            s_have_last_bssid = false;
        }
    }

    esp_wifi_set_config(WIFI_IF_STA, &config);
    esp_wifi_connect();
}

static void reconnect_timer_fired(void *arg)
{
    (void)arg;
    request_join();
}

static void schedule_join(void)
{
    /*
     * Never from inside the event handler when a delay is wanted: that
     * runs on the event task, and blocking it stalls every other event in
     * the system including the one that says the join succeeded.
     */
    if (s_retries < BACKOFF_AFTER_RETRIES) {
        request_join();
        return;
    }

    if (s_reconnect_timer == NULL) {
        const esp_timer_create_args_t args = {
            .callback = reconnect_timer_fired,
            .name = "oal_reconnect",
        };
        if (esp_timer_create(&args, &s_reconnect_timer) != ESP_OK) {
            request_join();
            return;
        }
    }
    esp_timer_stop(s_reconnect_timer);
    esp_timer_start_once(s_reconnect_timer, RECONNECT_DELAY_US);
}

static void on_wifi_event(void *arg, esp_event_base_t base, int32_t event_id, void *data)
{
    if (base == WIFI_EVENT && event_id == WIFI_EVENT_STA_START) {
        request_join();
    } else if (base == WIFI_EVENT && event_id == WIFI_EVENT_STA_DISCONNECTED) {
        s_retries++;

        /*
         * Giving up is only ever an answer at boot, where a wrong password
         * is the likely explanation and the portal is the cure. A node that
         * has held an address has demonstrably correct credentials, so
         * every later failure is a radio problem — and opening the portal
         * would take a working speaker off the network to ask a question
         * that has already been answered.
         */
        if (!s_was_connected && s_retries > MAX_STA_RETRIES) {
            xEventGroupSetBits(s_events, FAILED_BIT);
            return;
        }

        wifi_event_sta_disconnected_t *lost = (wifi_event_sta_disconnected_t *)data;
        ESP_LOGW(TAG, "disconnected (reason %d), attempt %d%s",
                 lost ? lost->reason : 0, s_retries,
                 (s_have_last_bssid && s_sticky_attempts < STICKY_ATTEMPTS)
                     ? ", asking for the same access point" : "");

        xEventGroupClearBits(s_events, CONNECTED_BIT);
        schedule_join();
    } else if (base == IP_EVENT && event_id == IP_EVENT_STA_GOT_IP) {
        ip_event_got_ip_t *event = (ip_event_got_ip_t *)data;

        /*
         * Which radio we landed on, remembered for next time. Reported too:
         * in a mesh every access point advertises the same SSID, so a roam
         * is completely invisible without the BSSID — and a producer and
         * its consumer drifting onto different radios is a three-hop path
         * where there was a two-hop one, which is audible.
         */
        wifi_ap_record_t ap;
        if (esp_wifi_sta_get_ap_info(&ap) == ESP_OK) {
            if (s_have_last_bssid && memcmp(s_last_bssid, ap.bssid, sizeof(ap.bssid)) != 0) {
                s_roams++;
                ESP_LOGW(TAG, "moved to a different access point: "
                              "%02x:%02x:%02x:%02x:%02x:%02x, ch %d, %d dBm (roam %u)",
                         ap.bssid[0], ap.bssid[1], ap.bssid[2],
                         ap.bssid[3], ap.bssid[4], ap.bssid[5],
                         (int)ap.primary, ap.rssi, (unsigned)s_roams);
            }
            memcpy(s_last_bssid, ap.bssid, sizeof(s_last_bssid));
            s_have_last_bssid = true;
        }

        ESP_LOGI(TAG, "got ip " IPSTR, IP2STR(&event->ip_info.ip));
        s_retries = 0;
        s_sticky_attempts = 0;
        s_was_connected = true;
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

    /*
     * No power save. The default is modem sleep: the radio wakes on the
     * beacon and the access point buffers unicast frames in between. That
     * is right for a device exchanging occasional control messages and
     * wrong for one taking 200 packets a second, where it costs steady
     * low-level loss and inflates jitter beyond anything the wire is
     * doing. A first measured link showed 0.24% loss and 2 ms of jitter
     * between two nodes on the same access point, which is far worse than
     * -61 dBm should give.
     *
     * The cost is current draw, which matters for the battery-powered
     * standalone mode of decision 4. Audio wins for now; a node that
     * knows it is idle could sleep again later.
     */
    ESP_ERROR_CHECK(esp_wifi_set_ps(WIFI_PS_NONE));

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

uint32_t oal_wifi_roams(void)
{
    return s_roams;
}

int oal_wifi_scan_json(char *out, size_t out_size)
{
    /*
     * A blocking scan. It stops the radio serving the connection for a
     * second or two, which on a playing node is a real cost — hence the
     * warning on the declaration, and hence this being a command rather
     * than something the Hub polls.
     */
    wifi_scan_config_t scan = { .show_hidden = false };
    if (esp_wifi_scan_start(&scan, true) != ESP_OK) {
        return -1;
    }

    uint16_t found = 0;
    if (esp_wifi_scan_get_ap_num(&found) != ESP_OK || found == 0) {
        return snprintf(out, out_size, "{\"aps\":[]}");
    }

    /* Enough to see a house's worth of radios without putting a kilobyte
     * of records on a task stack. */
    if (found > 12) {
        found = 12;
    }
    wifi_ap_record_t *records = calloc(found, sizeof(*records));
    if (records == NULL) {
        esp_wifi_scan_get_ap_num(&found);   /* drain, or the next scan fails */
        return -1;
    }
    if (esp_wifi_scan_get_ap_records(&found, records) != ESP_OK) {
        free(records);
        return -1;
    }

    /* Which one we are on, so the answer to "why that one" is right there
     * beside the alternatives rather than in another endpoint. */
    wifi_ap_record_t current;
    bool have_current = esp_wifi_sta_get_ap_info(&current) == ESP_OK;

    int len = snprintf(out, out_size, "{\"aps\":[");
    for (uint16_t i = 0; i < found && len > 0 && len < (int)out_size; i++) {
        const uint8_t *b = records[i].bssid;
        bool here = have_current && memcmp(b, current.bssid, 6) == 0;
        len += snprintf(out + len, out_size - (size_t)len,
                        "%s{\"ssid\":\"%s\","
                        "\"bssid\":\"%02x:%02x:%02x:%02x:%02x:%02x\","
                        "\"channel\":%u,\"rssi\":%d,\"current\":%s}",
                        i ? "," : "", (const char *)records[i].ssid,
                        b[0], b[1], b[2], b[3], b[4], b[5],
                        (unsigned)records[i].primary, (int)records[i].rssi,
                        here ? "true" : "false");
    }
    free(records);

    if (len <= 0 || len >= (int)out_size) {
        return -1;
    }
    len += snprintf(out + len, out_size - (size_t)len, "]}");
    return (len > 0 && len < (int)out_size) ? len : -1;
}

esp_err_t oal_wifi_rejoin(void)
{
    ESP_LOGW(TAG, "forgetting the current access point and scanning again");

    /* Only the memory the sticky rule is sticky about. The rule itself is
     * right — see the note beside STICKY_ATTEMPTS — and a board that has
     * been carried to another room is exactly the case it cannot know
     * about. */
    s_have_last_bssid = false;
    s_sticky_attempts = 0;

    wifi_config_t config = { 0 };
    if (esp_wifi_get_config(WIFI_IF_STA, &config) == ESP_OK) {
        /* Belt and braces: the driver persists its config, so a directed
         * join stored earlier must not survive into the scan. */
        config.sta.bssid_set = false;
        config.sta.scan_method = WIFI_ALL_CHANNEL_SCAN;
        config.sta.sort_method = WIFI_CONNECT_AP_BY_SIGNAL;
        esp_wifi_set_config(WIFI_IF_STA, &config);
    }

    esp_wifi_disconnect();
    return ESP_OK;   /* the disconnect handler schedules the join */
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
    "<label>Speaker</label>"
    "<div class=\"row\">"
    "<label><input type=\"radio\" name=\"channel\" value=\"stereo\" checked> Stereo pair</label>"
    "<label><input type=\"radio\" name=\"channel\" value=\"mono\"> Single (mono)</label>"
    "<label><input type=\"radio\" name=\"channel\" value=\"left\"> Left only</label>"
    "<label><input type=\"radio\" name=\"channel\" value=\"right\"> Right only</label>"
    "</div>"
    "<label>Audio leaves the board by</label>"
    "<div class=\"row\">"
    "<label><input type=\"radio\" name=\"output\" value=\"i2s\" checked> I2S DAC</label>"
    "<label><input type=\"radio\" name=\"output\" value=\"usb\"> USB dongle</label>"
    "</div>"
    "<button type=\"submit\">Save and reboot</button></form></body></html>";

/* Built once at portal start, because the default name comes from the MAC.
 * The page renders to about 2 KB with the role, speaker and output rows;
 * the slack is so an edit to the form does not silently become a portal
 * that refuses to start, which is the one failure a node cannot report. */
static char s_portal_page[3072];

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
    char channel[16] = { 0 };
    char output[16] = { 0 };

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

    /* Same reasoning as the roles: a value this build does not recognise
     * should be refused while the node is still on its own access point. */
    if (httpd_query_key_value(form, "channel", channel, sizeof(channel)) == ESP_OK) {
        url_decode(channel);
        oal_channel_t parsed_channel;
        if (!oal_channel_parse(channel, &parsed_channel)) {
            ESP_LOGE(TAG, "unrecognised channel \"%s\"", channel);
            httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "unknown channel");
            return ESP_FAIL;
        }
        if (oal_config_set_channel(parsed_channel) != ESP_OK) {
            httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "could not save channel");
            return ESP_FAIL;
        }
    }

    /* The output stage (docs/USB-AUDIO.md). Absent is not an error: the
     * form always sends it, but the same handler serves anything posting
     * to this endpoint, and a node that omits it keeps the DAC default. */
    if (httpd_query_key_value(form, "output", output, sizeof(output)) == ESP_OK) {
        url_decode(output);
        oal_output_t parsed_output;
        if (!oal_output_parse(output, &parsed_output)) {
            ESP_LOGE(TAG, "unrecognised output \"%s\"", output);
            httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "unknown output");
            return ESP_FAIL;
        }
        if (oal_config_set_output(parsed_output) != ESP_OK) {
            httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "could not save output");
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

    ESP_LOGI(TAG, "provisioned: ssid \"%s\", name \"%s\", roles \"%s\", channel \"%s\", output \"%s\"; rebooting",
             ssid, name[0] ? name : "(default)", roles[0] ? roles : "(default)",
             channel[0] ? channel : "(default)",
             output[0] ? output : "(default)");
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
        return httpd_resp_send(req, s_portal_page, HTTPD_RESP_USE_STRLEN);
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
/*
 * Whether the portal is up because the network was missing, or because the
 * node has never been told what network to look for. Only the first is
 * worth retrying: there is nothing to retry towards when NVS is empty.
 */
static bool s_had_credentials;

/* The network being retried towards, kept for the log line that says so.
 * The copy in `oal_wifi_start` is a local and gone by the time the timer
 * fires. */
static char s_ssid[33];

/* Long enough that a household router is back before the second attempt,
 * short enough that nobody stands in front of a silent speaker wondering. */
#define PORTAL_RETRY_US (180 * 1000000ULL)

static esp_timer_handle_t s_portal_retry_timer;

/*
 * Try the network again by starting over.
 *
 * The portal cannot simply add a station interface and look: `start_portal`
 * runs a plain AP on purpose, because with AP+STA up the access point's
 * DHCP server stops handing out leases and a phone joins to no address.
 * Tearing the AP down, trying the station, and rebuilding it on failure is
 * three states to get wrong.
 *
 * Rebooting is one. The boot path already tries the station properly —
 * every channel, strongest first, ten attempts — and already falls back
 * here if that fails, so a restart *is* the retry, using the code most
 * exercised in the project rather than a second implementation of it.
 *
 * Why this is needed at all: the portal used to be terminal. A power cut
 * takes the router and the nodes down together, the nodes boot in fifteen
 * seconds and give up at thirty, and the router is still coming up a minute
 * later — so every node in the house ended up in a setup portal, and
 * nothing recovered them but rebooting each one by hand.
 *
 * Never while somebody is connected. A phone on the portal is a person
 * halfway through typing a password, and cutting that off to go and look
 * for a network they are in the middle of telling us about would be its own
 * kind of stupid.
 */
static void portal_retry_fired(void *arg)
{
    wifi_sta_list_t stations;
    if (esp_wifi_ap_get_sta_list(&stations) == ESP_OK && stations.num > 0) {
        ESP_LOGI(TAG, "portal has %d client(s); not retrying the network yet",
                 (int)stations.num);
        return;
    }

    ESP_LOGW(TAG, "nobody on the portal — restarting to try \"%s\" again", s_ssid);
    esp_restart();
}

static void start_portal_retry(void)
{
    if (!s_had_credentials) {
        ESP_LOGI(TAG, "no stored credentials, so nothing to retry towards");
        return;
    }

    const esp_timer_create_args_t args = {
        .callback = portal_retry_fired,
        .name = "oal_portal_retry",
    };
    if (esp_timer_create(&args, &s_portal_retry_timer) != ESP_OK) {
        ESP_LOGE(TAG, "could not create the portal retry timer");
        return;
    }
    esp_timer_start_periodic(s_portal_retry_timer, PORTAL_RETRY_US);
    ESP_LOGI(TAG, "will retry the stored network every %d s while the portal is idle",
             (int)(PORTAL_RETRY_US / 1000000ULL));
}

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

    start_portal_retry();
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
    if (load_pair("wifi_ssid", "wifi_pass", ssid, sizeof(ssid),
                  password, sizeof(password)) != ESP_OK) {
        if (fallback_ssid != NULL && fallback_ssid[0] != '\0') {
            strlcpy(ssid, fallback_ssid, sizeof(ssid));
            strlcpy(password, fallback_password != NULL ? fallback_password : "", sizeof(password));
        }
    }

    char party_ssid[33] = { 0 };
    char party_password[65] = { 0 };
    (void)load_pair("party_ssid", "party_pass", party_ssid, sizeof(party_ssid),
                    party_password, sizeof(party_password));

    s_had_credentials = ssid[0] != '\0' || party_ssid[0] != '\0';
    strlcpy(s_ssid, ssid[0] != '\0' ? ssid : party_ssid, sizeof(s_ssid));

    /*
     * Home first, then the party network. Order is the whole design.
     *
     * At home the second attempt never happens, because the first
     * succeeds. At a venue the first cannot succeed — the house network is
     * miles away — so the node spends its thirty seconds failing and then
     * finds the island. Same rule in both places, which is why a consumer
     * needs no mode, no flag, and nothing done to it before an event or
     * after one.
     *
     * The thirty seconds are the price, paid once at power-up at the venue
     * and never at home. Cheaper than a state a person has to remember to
     * set and remember to unset.
     */
    if (ssid[0] != '\0' && try_station(ssid, password)) {
        return OAL_WIFI_STA;
    }

    if (party_ssid[0] != '\0') {
        ESP_LOGW(TAG, "falling back to the party network \"%s\"", party_ssid);
        if (try_station(party_ssid, party_password)) {
            return OAL_WIFI_STA;
        }
    }

    start_portal();
    return OAL_WIFI_PORTAL;
}
