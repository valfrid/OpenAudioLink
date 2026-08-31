#include "oal_control.h"

#include <inttypes.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>

#include "cJSON.h"
#include "oal_capture.h"
#include "oal_config.h"
#include "oal_discovery.h"
#include "oal_join.h"
#include "oal_playout.h"
#include "oal_stream.h"
#include "oal_wifi.h"

#include "node_page.h"
#include "esp_http_server.h"
#include "lwip/sockets.h"
#include "esp_https_ota.h"
#include "esp_ota_ops.h"
#include "esp_log.h"
#include "esp_system.h"
#include "esp_timer.h"
#include "esp_wifi.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

static const char *TAG = "oal_control";

#define CONTROL_PORT 41001
#define PROTOCOL_VERSION "0.1"

/*
 * A node must not accept a delay the ring cannot hold. The alternative is
 * silent clamping: a setting that reads back differently from what was
 * asked for, on the one screen built to say what a node is doing.
 *
 * This was a static assertion against OAL_PLAYOUT_MAX_TARGET_MS, and it
 * cannot be one any more -- the ring is a setting now, so the limit is not
 * known until the output stage has started. It became a function on the
 * playout instead, asked at request time, and `/status` publishes the
 * answer so the Hub can offer the real range rather than a remembered one.
 *
 * That is not a weakening of the original intent, it is the same intent
 * moved somewhere it can still be true. What the assertion was protecting
 * against -- two numbers describing one limit, drifting apart -- is now
 * impossible rather than merely checked, because there is only one number
 * and everyone asks for it.
 */
/*
 * The live ring where there is one, the stored size where there is not.
 *
 * Live first, because after a rollback the stored value may be one this
 * firmware never allocated, and the ring in front of the speaker is the one
 * worth reporting.
 *
 * But a producer has no playout at all, and reporting 0 there put two
 * fields describing two different rings beside each other on the same
 * screen: `ringMs` 0 next to a `maxDelayMs` of 200, which is derived from a
 * stored 400. One of the two had to be wrong, and it was the one claiming
 * the node has no buffer setting -- it has one, and would use it the moment
 * it was given a consumer role.
 */
static uint32_t reported_ring_ms(void)
{
    uint32_t live = oal_playout_ring_ms();
    return live != 0 ? live : oal_config_get_ring_ms();
}

static uint32_t delay_ceiling(void)
{
    uint32_t max_target = oal_playout_max_target_ms();

    /*
     * Nothing playing yet -- a producer with no output stage, or a consumer
     * whose sink failed to open -- so answer for the ring this node is
     * configured to build at its next boot rather than refusing everything.
     *
     * Refusing would be the tidier-looking branch and the wrong one: the
     * node that most needs to be reconfigured over the network is the one
     * whose output stage did not come up, and that is exactly the case
     * where the live ring is zero.
     */
    if (max_target == 0) {
        max_target = oal_config_get_ring_ms() / 4 * 3;
    }
    if (max_target <= CONFIG_OAL_PLAYOUT_MS) {
        return 0;
    }
    uint32_t room = max_target - CONFIG_OAL_PLAYOUT_MS;
    return room > OAL_DELAY_MS_MAX ? OAL_DELAY_MS_MAX : room;
}

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
                    "\"channel\":%d,\"rssi\":%d,\"roams\":%" PRIu32 ","
                    "\"disconnects\":%" PRIu32 ",\"lastReason\":%d}",
                    ssid, ap.bssid[0], ap.bssid[1], ap.bssid[2],
                    ap.bssid[3], ap.bssid[4], ap.bssid[5],
                    (int)ap.primary, ap.rssi, oal_wifi_roams(),
                    oal_wifi_disconnects(), oal_wifi_last_reason());
}

/*
 * Who this node believes holds the Controller role, and whether it has been
 * answered (decision 9).
 *
 * Without this the whole house case is invisible: a Consumer asks, the Hub
 * says stand by, and nothing happens — which is correct and looks exactly
 * like nothing working. A node has to be able to say who it is listening
 * to.
 */
static int format_controller(char *out, size_t out_size)
{
    oal_peer_t controller;
    switch (oal_discovery_controller(&controller)) {
    case OAL_CONTROLLER_SELF:
        return snprintf(out, out_size, "{\"who\":\"self\"}");
    case OAL_CONTROLLER_PEER:
        return snprintf(out, out_size,
                        "{\"who\":\"peer\",\"id\":\"%s\",\"name\":\"%s\","
                        "\"address\":\"%s\"}",
                        controller.id, controller.name, controller.address);
    default:
        return snprintf(out, out_size, "{\"who\":\"none\"}");
    }
}

/*
 * Which image is running, whether it is confirmed, and what became of the
 * other one.
 *
 * Rollback without this is worse than no rollback: a reverted node comes
 * back online, joined, streaming, reporting the *old* version, looking
 * entirely normal. The update would read as one that never arrived, and
 * "the download failed" and "the image installed and rejected itself" want
 * completely different responses.
 *
 * The bootloader does say so over the console. The node that most needs to
 * be heard is the one whose output stage owns the peripheral the console
 * would use, which is the same reason outputArrivedAs and the ring's
 * low-water mark ended up here.
 *
 * `otherState` is the signal. After a rollback the running slot reads
 * valid -- it is the restored image, and it is fine -- while the slot that
 * was just tried reads aborted or invalid.
 */
static const char *ota_state_name(esp_ota_img_states_t state)
{
    switch (state) {
    case ESP_OTA_IMG_NEW:            return "new";
    case ESP_OTA_IMG_PENDING_VERIFY: return "pending";
    case ESP_OTA_IMG_VALID:          return "valid";
    case ESP_OTA_IMG_INVALID:        return "invalid";
    case ESP_OTA_IMG_ABORTED:        return "aborted";
    default:                         return "undefined";
    }
}

static const char *reset_reason_name(void)
{
    switch (esp_reset_reason()) {
    case ESP_RST_POWERON:  return "power-on";
    case ESP_RST_EXT:      return "reset pin";
    case ESP_RST_SW:       return "software";
    case ESP_RST_PANIC:    return "panic";
    case ESP_RST_INT_WDT:  return "interrupt watchdog";
    case ESP_RST_TASK_WDT: return "task watchdog";
    case ESP_RST_WDT:      return "watchdog";
    case ESP_RST_BROWNOUT: return "brownout";
    case ESP_RST_DEEPSLEEP: return "deep sleep";
    default:               return "unknown";
    }
}

static int format_ota(char *out, size_t out_size)
{
    const esp_partition_t *running = esp_ota_get_running_partition();
    if (running == NULL) {
        return snprintf(out, out_size, "null");
    }

    esp_ota_img_states_t state = ESP_OTA_IMG_UNDEFINED;
    esp_ota_get_state_partition(running, &state);

    /* The slot that is not running: after a rollback this is the image
     * that was rejected, and it is the only place that says so. */
    const esp_partition_t *other = esp_ota_get_next_update_partition(NULL);
    esp_ota_img_states_t other_state = ESP_OTA_IMG_UNDEFINED;
    const char *other_label = "?";
    if (other != NULL) {
        esp_ota_get_state_partition(other, &other_state);
        other_label = other->label;
    }

    return snprintf(out, out_size,
                    "{\"slot\":\"%s\",\"state\":\"%s\","
                    "\"otherSlot\":\"%s\",\"otherState\":\"%s\","
                    "\"resetReason\":\"%s\"}",
                    running->label, ota_state_name(state),
                    other_label, ota_state_name(other_state),
                    reset_reason_name());
}

/*
 * Response buffers live here rather than on the stack.
 *
 * They total about 1.6 kB, and the httpd task's whole stack was 4 kB — the
 * esp_http_server default. Parsing a request, running a handler and sending
 * a reply all happen on that one stack, and an interrupt taken at the
 * deepest point pushes its frame onto it too. It overflowed on the vinyl
 * node:
 *
 *     ***ERROR*** A stack overflow in task httpd has been detected.
 *
 * with the backtrace corrupted, which is what an overflow looks like once
 * the frame that would have explained it has been written over.
 *
 * Static is safe because esp_http_server runs exactly one task per
 * instance and multiplexes every session onto it with select(), so two
 * handlers never run at once. peers_handler already relies on this for its
 * peer array. **The invariant is one httpd task per instance** — a second
 * control server in this process would need these per-instance instead.
 * The provisioning portal in oal_wifi.c is a separate instance with its own
 * handlers and does not touch these.
 */
static char s_roles[OAL_ROLES_STR_MAX];
static char s_wifi[256];
static char s_controller[160];
static char s_join[96];
static char s_input[112];
static char s_ota[176];
/*
 * /status, sized against its measured worst case rather than by eye.
 *
 * 1 060 bytes with every field saturated -- a 32-character id, a long
 * node name, a full-length SSID, and the partyReady and delayMs fields
 * added since this was 1 024. It had not failed yet only because the
 * names in this house are short, which is not a property to rely on.
 *
 * Same lesson as s_stream_body below, found in the same hour: measure the
 * format, do not estimate it.
 */
static char s_body[1536];

/* ---------- GET / ---------- */

/*
 * A node's own page, so it can be operated with nothing else present.
 *
 * Static: everything on it is fetched by its own script from /status,
 * /peers and /stream. Formatting HTML in C is how this project produced
 * four red builds from one misordered snprintf, and a page that renders
 * itself from the same endpoints the Hub uses cannot drift away from them.
 */
static esp_err_t root_handler(httpd_req_t *req)
{
    httpd_resp_set_type(req, "text/html");
    return httpd_resp_send(req, NODE_PAGE, HTTPD_RESP_USE_STRLEN);
}

static esp_err_t status_handler(httpd_req_t *req)
{
    char *roles = s_roles;
    if (oal_roles_to_json(s_config.roles, roles, sizeof(s_roles)) < 0) {
        httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "roles too large");
        return ESP_FAIL;
    }

    char *wifi = s_wifi;
    int wifi_len = format_wifi(wifi, sizeof(s_wifi));
    if (wifi_len <= 0 || wifi_len >= (int)sizeof(s_wifi)) {
        httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "wifi status too large");
        return ESP_FAIL;
    }

    char *controller = s_controller;
    if (format_controller(controller, sizeof(s_controller)) >= (int)sizeof(s_controller)) {
        snprintf(controller, sizeof(s_controller), "{\"who\":\"none\"}");
    }

    /* Only a Consumer joins, so only a Consumer has anything to report
     * about it. A producer saying "not asked" would read as a failure.
     *
     * Reset explicitly: a static buffer keeps the last request's answer,
     * where the initialiser on a local ran every time. */
    char *join = s_join;
    snprintf(join, sizeof(s_join), "null");
    if ((s_config.roles & OAL_ROLE_CONSUMER) != 0) {
        snprintf(join, sizeof(s_join), "{\"asked\":%s,\"status\":\"%s\"}",
                 oal_join_acknowledged() ? "true" : "false", oal_join_last_status());
    }

    /*
     * What the analog input is hearing, or null on a node with no ADC.
     *
     * In /status rather than /stream because it has to be answerable
     * *before* anything is streaming: the question it exists for is "is the
     * turntable wired up", asked by somebody standing at the turntable with
     * nothing playing. /stream only says anything while a stream runs, which
     * is exactly the wrong time.
     */
    char *ota = s_ota;
    if (format_ota(ota, sizeof(s_ota)) >= (int)sizeof(s_ota)) {
        snprintf(ota, sizeof(s_ota), "null");
    }

    char *input = s_input;
    snprintf(input, sizeof(s_input), "null");
    if (oal_capture_running()) {
        oal_capture_state_t capture;
        oal_capture_get(&capture);
        snprintf(input, sizeof(s_input),
                 "{\"leftDb\":%d,\"rightDb\":%d,\"hz\":%" PRIu32 ",\"readErrors\":%" PRIu32 "}",
                 capture.peak_left_dbfs, capture.peak_right_dbfs,
                 capture.measured_hz, capture.read_errors);
    }

    /*
     * How much of the httpd task's stack has never been used, in bytes
     * (ESP-IDF's port reports this in bytes, not words). Reported because
     * the overflow that made these buffers static was invisible until it
     * was fatal: the node ran for eighty seconds and then rebooted with a
     * corrupted backtrace. A number in /status turns "it seems stable now"
     * into a margin somebody can watch, and the Hub already polls this
     * every five seconds.
     */
    const char *arrived = oal_playout_output_arrived_as();

    char *body = s_body;
    int len = snprintf(body, sizeof(s_body),
                       "{\"oal\":\"" PROTOCOL_VERSION "\",\"id\":\"%s\",\"name\":\"%s\","
                       "\"roles\":%s,\"channel\":\"%s\",\"volume\":%u,"
                       /* Which output stage the board has, and whether it
                        * can currently play through it. A dongle node with
                        * nothing plugged in is receiving, buffering and
                        * silent, and that is the only place it says so. */
                       "\"output\":\"%s\",\"outputReady\":%s,"
                       /* What the output stage was holding when we opened
                        * it. A dongle node has no console — its output
                        * stage owns the USB peripheral the log would use —
                        * so the line that explains a silent speaker has to
                        * arrive here or nowhere. */
                       "\"outputArrivedAs\":%s%s%s,"
                       "\"input\":%s,"
                       /* `input` above is what the ADC is *hearing*; this
                        * is what the node is *wired to*. Two meanings of
                        * one word, so the setting gets the longer name
                        * rather than the live reading losing its own --
                        * every client already reads `input` as levels. */
                       "\"inputStage\":\"%s\","
                       "\"hw\":\"%s\",\"fw\":\"%s\","
                       "\"uptimeS\":%lld,\"heapFree\":%u,\"wifi\":%s,"
                       /* Whether, never what. This document is polled by
                        * the Hub, the switchboard and every node's own
                        * page; a passphrase does not belong in it. */
                       "\"partyReady\":%s,\"delayMs\":%u,"
                       /* The ring as allocated and the two limits that
                        * follow from it. Published rather than assumed
                        * because assuming is how the Hub came to offer
                        * 0-200 ms against a real ceiling of 50 -- and with
                        * the ring settable there is no longer any constant
                        * the Hub could have hardcoded correctly. */
                       "\"ringMs\":%u,\"maxTargetMs\":%u,\"maxDelayMs\":%u,"
                       "\"ota\":%s,"
                       "\"controller\":%s,\"join\":%s,"
                       "\"httpdStackFreeB\":%u,"
                       "\"audio\":{\"state\":\"idle\"}}",
                       s_config.id, s_config.name, roles,
                       oal_channel_name(oal_config_get_channel()),
                       /* What the speaker is actually doing, not what is
                        * stored: they differ for as long as it takes an
                        * NVS write to fail, and the sound is the truth. */
                       (unsigned)oal_playout_volume(),
                       oal_output_name(oal_config_get_output()),
                       oal_playout_output_ready() ? "true" : "false",
                       arrived ? "\"" : "", arrived ? arrived : "null",
                       arrived ? "\"" : "",
                       input, oal_input_name(oal_config_get_input()),
                       s_config.hardware_profile, s_config.firmware_version,
                       (long long)(esp_timer_get_time() / 1000000),
                       (unsigned)esp_get_free_heap_size(), wifi,
                       oal_wifi_has_party() ? "true" : "false",
                       (unsigned)oal_config_get_delay_ms(),
                       (unsigned)reported_ring_ms(),
                       (unsigned)oal_playout_max_target_ms(),
                       (unsigned)delay_ceiling(), ota,
                       controller, join,
                       (unsigned)uxTaskGetStackHighWaterMark(NULL));
    if (len <= 0 || len >= (int)sizeof(s_body)) {
        /* Loud: from outside, a response that will not fit looks exactly
         * like a node that has stopped answering. */
        ESP_LOGE(TAG, "status needs %d bytes, buffer is %u",
                 len, (unsigned)sizeof(s_body));
        httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "status too large");
        return ESP_FAIL;
    }

    httpd_resp_set_type(req, "application/json");
    return httpd_resp_send(req, body, len);
}

/* ---------- POST /config ---------- */

/*
 * Sets what this node is, what it plays, and where it goes when home is
 * not there:
 * {"roles":["consumer"],"channel":"mono","party":{"ssid":..,"password":..}}
 * Any field alone is valid.
 *
 * Stored in NVS and applied at the next boot. Roles decide which tasks
 * start, and the channel decides what the playout does with each frame;
 * changing either under a running stream would mean tearing down live
 * audio, and a reboot is both simpler and more honest about what happened.
 *
 * The party network is the same pair on every node in a group (decision
 * 4). It is written here rather than through the provisioning portal
 * because both nodes must hold *identical* credentials, and two people
 * typing one passphrase into two phones is precisely how that fails. The
 * Hub generates it once and pushes it to everything while it is all still
 * on the desk.
 *
 * An empty ssid forgets it, which is how a node leaves a group.
 */
static esp_err_t config_handler(httpd_req_t *req)
{
    char body[384];
    int len = httpd_req_recv(req, body, sizeof(body) - 1);
    if (len <= 0) {
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "empty body");
        return ESP_FAIL;
    }
    body[len] = '\0';

    cJSON *root = cJSON_ParseWithLength(body, len);
    if (root == NULL) {
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "bad json");
        return ESP_FAIL;
    }

    const cJSON *array = cJSON_GetObjectItemCaseSensitive(root, "roles");
    const cJSON *channel = cJSON_GetObjectItemCaseSensitive(root, "channel");

    /* Which fields were present, recorded now. Everything below happens
     * after the tree is freed, and testing a pointer into a freed tree is
     * the kind of bug that works until the allocator is under pressure. */
    const cJSON *output = cJSON_GetObjectItemCaseSensitive(root, "output");
    const cJSON *input = cJSON_GetObjectItemCaseSensitive(root, "input");
    const cJSON *delay = cJSON_GetObjectItemCaseSensitive(root, "delayMs");
    const cJSON *party = cJSON_GetObjectItemCaseSensitive(root, "party");
    const cJSON *ring = cJSON_GetObjectItemCaseSensitive(root, "ringMs");
    const cJSON *name = cJSON_GetObjectItemCaseSensitive(root, "name");
    const bool has_roles = cJSON_IsArray(array);
    const bool has_channel = cJSON_IsString(channel);
    const bool has_party = cJSON_IsObject(party);
    const bool has_output = cJSON_IsString(output);
    const bool has_input = cJSON_IsString(input);
    const bool has_delay = cJSON_IsNumber(delay);
    const bool has_ring = cJSON_IsNumber(ring);
    const bool has_name = cJSON_IsString(name);
    const uint32_t delay_ms = has_delay ? (uint32_t)delay->valueint : 0;
    const uint32_t ring_ms = has_ring ? (uint32_t)ring->valueint : 0;

    /* Either may be set alone: changing a speaker from stereo to mono has
     * nothing to do with whether it is still a consumer, and requiring
     * both would make one setting able to clobber the other. */
    if (!has_roles && !has_channel && !has_party && !has_delay && !has_output
            && !has_ring && !has_input && !has_name) {
        cJSON_Delete(root);
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST,
                            "expected roles, channel, output, input, name, delayMs, "
                            "ringMs or party");
        return ESP_FAIL;
    }

    /*
     * The name, copied out before the tree is freed like everything else.
     *
     * Length is checked here rather than left to oal_config_set_name, so a
     * too-long name is a 400 that says the limit instead of a bare "not
     * stored". OAL_NAME_MAX is the announce field's width, so a name that
     * does not fit is one no other device could ever display.
     */
    char wanted_name[OAL_NAME_MAX];
    wanted_name[0] = '\0';
    if (has_name) {
        if (strlen(name->valuestring) >= OAL_NAME_MAX) {
            cJSON_Delete(root);
            httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST,
                                "name too long");
            return ESP_FAIL;
        }
        snprintf(wanted_name, sizeof(wanted_name), "%s", name->valuestring);
    }

    /*
     * The output stage, and the reason it is settable from here at all.
     *
     * `oal_config_set_output` existed from the day the USB sink did, and
     * nothing ever called it: the stage was written during provisioning
     * and after that a node was whatever it had been told to be. That was
     * fine until a firmware change left a USB node short of heap -- the
     * dongle would not open, and neither would OTA, because
     * esp_https_ota allocates from the same pool. A node that cannot be
     * fixed remotely because of the fault being fixed.
     *
     * This is the way out. Telling a starved node to come up on I²S costs
     * it nothing to receive, and the reboot that follows leaves the USB
     * host stack uninstalled and the heap free enough to take an update.
     */
    oal_output_t wanted_output = OAL_OUTPUT_DEFAULT;
    if (has_output && !oal_output_parse(output->valuestring, &wanted_output)) {
        cJSON_Delete(root);
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "unknown output");
        return ESP_FAIL;
    }

    /*
     * The input stage, and why it is refused rather than guessed at.
     *
     * It picks a set of pins *and* which end drives the clocks: the
     * microphone is a slave and needs them, the self-clocked ADC module
     * supplies its own. Falling back to a default on a typo would put two
     * drivers on one line, and HARDWARE.md already records the symptom --
     * silence, with nothing to say why.
     */
    oal_input_t wanted_input = OAL_INPUT_DEFAULT;
    if (has_input && !oal_input_parse(input->valuestring, &wanted_input)) {
        cJSON_Delete(root);
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "unknown input");
        return ESP_FAIL;
    }
    /* Against what this node's ring can actually give, not a constant. A
     * node with a 1000 ms ring accepts 650; one still on the 200 ms default
     * accepts 50; and either way the number in the error is the true one,
     * so a rejection tells you the limit instead of just naming it. */
    if (has_delay
            && (delay->valueint < 0 || (uint32_t)delay->valueint > delay_ceiling())) {
        cJSON_Delete(root);
        ESP_LOGW(TAG, "refused delayMs %d; this ring allows up to %" PRIu32,
                 delay->valueint, delay_ceiling());
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "delayMs out of range");
        return ESP_FAIL;
    }
    if (has_ring
            && (ring_ms < OAL_RING_MS_MIN || ring_ms > OAL_RING_MS_MAX)) {
        cJSON_Delete(root);
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "ringMs out of range");
        return ESP_FAIL;
    }

    /*
     * Copied out before the tree is freed, and sized to what the radio
     * accepts rather than to what arrived: an over-long SSID silently
     * truncated is a node that joins nothing and cannot say why.
     */
    char party_ssid[33] = { 0 };
    char party_password[65] = { 0 };
    if (has_party) {
        const cJSON *pssid = cJSON_GetObjectItemCaseSensitive(party, "ssid");
        const cJSON *ppass = cJSON_GetObjectItemCaseSensitive(party, "password");
        if (!cJSON_IsString(pssid)) {
            cJSON_Delete(root);
            httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "party needs an ssid");
            return ESP_FAIL;
        }
        if (strlen(pssid->valuestring) >= sizeof(party_ssid)) {
            cJSON_Delete(root);
            httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "party ssid too long");
            return ESP_FAIL;
        }
        /* Empty ssid forgets the network, and then a password is neither
         * required nor meaningful. */
        if (pssid->valuestring[0] != '\0') {
            if (!cJSON_IsString(ppass)) {
                cJSON_Delete(root);
                httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "party needs a password");
                return ESP_FAIL;
            }
            if (strlen(ppass->valuestring) >= sizeof(party_password)) {
                cJSON_Delete(root);
                httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "party password too long");
                return ESP_FAIL;
            }
            strlcpy(party_password, ppass->valuestring, sizeof(party_password));
        }
        strlcpy(party_ssid, pssid->valuestring, sizeof(party_ssid));
    }

    oal_roles_t roles = OAL_ROLE_NONE;
    if (has_roles) {
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
        if (roles == OAL_ROLE_NONE) {
            cJSON_Delete(root);
            httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "at least one role is required");
            return ESP_FAIL;
        }
    }

    oal_channel_t wanted = OAL_CHANNEL_DEFAULT;
    if (has_channel && !oal_channel_parse(channel->valuestring, &wanted)) {
        cJSON_Delete(root);
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "unknown channel");
        return ESP_FAIL;
    }
    cJSON_Delete(root);

    /* Both validated before either is written, so a bad second field
     * cannot leave the node half-reconfigured. */
    if (has_roles && oal_config_set_roles(roles) != ESP_OK) {
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "roles not stored");
        return ESP_FAIL;
    }
    if (has_channel && oal_config_set_channel(wanted) != ESP_OK) {
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "channel not stored");
        return ESP_FAIL;
    }
    /*
     * Stored and applied at once, unlike roles and channel.
     *
     * Those two decide which tasks exist and what the playout does with
     * each frame, so they wait for a reboot. This one only moves a target
     * the servo is already chasing, and it is tuned by ear against another
     * speaker in the room — a value that needed a reboot to hear would
     * make that a twenty-minute job instead of a two-minute one.
     */
    /* At the next boot, like roles: the sink is chosen when playout
     * starts, and swapping it under a running stream would mean tearing
     * down live audio to change something that describes the box. */
    if (has_output && oal_config_set_output(wanted_output) != ESP_OK) {
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "output not stored");
        return ESP_FAIL;
    }
    /* At the next boot too, and for a stronger reason than the output
     * stage: the capture path's pins and clock role are fixed when I2S is
     * installed, and swapping them under a running capture is not a thing
     * the driver offers. */
    if (has_input && oal_config_set_input(wanted_input) != ESP_OK) {
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "input not stored");
        return ESP_FAIL;
    }
    /*
     * The name takes effect at once, unlike everything around it.
     *
     * The others decide which tasks start or which pins the I2S driver
     * claims, so they wait for a boot. A name decides nothing: it is a
     * label, carried on the announce and shown in lists. Making a typo
     * wait for a reboot is the kind of friction that stops people fixing
     * it, and the next announce is a few seconds away.
     *
     * An empty string is a deliberate erase, restoring the MAC-derived
     * default, so this is not guarded on the name being non-empty.
     */
    if (has_name) {
        if (oal_config_set_name(wanted_name) != ESP_OK) {
            httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "name not stored");
            return ESP_FAIL;
        }
        oal_config_get_name(s_config.name, sizeof(s_config.name));
        /* Both, or the rename is only half done: NVS decides what the node
         * calls itself after the next boot, the announce decides what every
         * list on the network shows right now. */
        if (oal_discovery_set_name(s_config.name) != ESP_OK) {
            ESP_LOGW(TAG, "renamed to \"%s\" but the announce kept the old name",
                     s_config.name);
        } else {
            ESP_LOGI(TAG, "renamed to \"%s\"", s_config.name);
        }
    }
    if (has_delay) {
        if (oal_config_set_delay_ms(delay_ms) != ESP_OK) {
            httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "delay not stored");
            return ESP_FAIL;
        }
        (void)oal_playout_set_target_ms(CONFIG_OAL_PLAYOUT_MS + delay_ms);
    }
    /* At the next boot, unlike the delay just above, and the difference is
     * not arbitrary: the delay moves a target the servo is already walking
     * towards, while this frees and reallocates the buffer the playout task
     * is reading out of. */
    if (has_ring && oal_config_set_ring_ms(ring_ms) != ESP_OK) {
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "ringMs not stored");
        return ESP_FAIL;
    }
    if (has_party && oal_wifi_set_party(party_ssid, party_password) != ESP_OK) {
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "party network not stored");
        return ESP_FAIL;
    }

    char stored[OAL_ROLES_STR_MAX];
    char response[256];
    oal_roles_to_json(oal_config_get_roles(), stored, sizeof(stored));
    int n = snprintf(response, sizeof(response),
                     "{\"status\":\"stored\",\"roles\":%s,\"channel\":\"%s\","
                     "\"output\":\"%s\",\"input\":\"%s\",\"name\":\"%s\","
                     "\"partyReady\":%s,\"ringMs\":%" PRIu32 ","
                     "\"appliesAt\":\"%s\"}",
                     stored, oal_channel_name(oal_config_get_channel()),
                     oal_output_name(oal_config_get_output()),
                     oal_input_name(oal_config_get_input()), s_config.name,
                     oal_wifi_has_party() ? "true" : "false",
                     oal_config_get_ring_ms(),
                     /* Truthful per request rather than per endpoint. A
                      * name lands at once; everything else here decides
                      * which tasks start or which pins are claimed, and
                      * waits for a boot. Saying "reboot" after a rename
                      * that already happened invites a pointless one. */
                     (has_name && !has_roles && !has_channel && !has_output
                      && !has_input && !has_delay && !has_ring && !has_party)
                         ? "now" : "reboot");
    httpd_resp_set_type(req, "application/json");
    return httpd_resp_send(req, response, n);
}

/* ---------- POST /volume ---------- */

/*
 * {"percent":40}. Applies immediately and persists.
 *
 * A separate endpoint from /config, and not merged into it, because the
 * two differ in the only way that matters to whoever calls them: /config
 * stores a setting that arrives at the next reboot, and this one changes
 * what the room sounds like before the response is written. Sharing a
 * route would mean one reply saying "appliesAt: reboot" about one field
 * and not the other.
 *
 * Storing it is the second-order concern, so a node whose NVS write fails
 * still turns down. Somebody is standing at a slider; the sound is the
 * answer they are waiting for, and it being forgotten by tomorrow is a
 * smaller problem than it not happening now.
 */
static esp_err_t volume_handler(httpd_req_t *req)
{
    char body[96];
    int len = httpd_req_recv(req, body, sizeof(body) - 1);
    if (len <= 0) {
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "empty body");
        return ESP_FAIL;
    }
    body[len] = '\0';

    cJSON *root = cJSON_ParseWithLength(body, len);
    if (root == NULL) {
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "bad json");
        return ESP_FAIL;
    }

    const cJSON *percent = cJSON_GetObjectItemCaseSensitive(root, "percent");
    if (!cJSON_IsNumber(percent) || percent->valuedouble < 0 || percent->valuedouble > 100) {
        cJSON_Delete(root);
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "percent must be 0 to 100");
        return ESP_FAIL;
    }
    uint8_t wanted = (uint8_t)percent->valueint;
    cJSON_Delete(root);

    oal_playout_set_volume(wanted);
    bool stored = oal_config_set_volume(wanted) == ESP_OK;

    char response[96];
    int n = snprintf(response, sizeof(response),
                     "{\"status\":\"set\",\"volume\":%u,\"stored\":%s}",
                     (unsigned)oal_playout_volume(), stored ? "true" : "false");
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
/*
 * Off the stack for the reason the /status buffers are, and more urgently:
 * this is the deepest handler in the file, and it went from occasional to
 * every five seconds when the Hub grew a supervisor that watches a running
 * stream. The crash showed up on a producer, which is the branch below
 * that carries both a destination list and the counters.
 */
/*
 * Sized against the measured worst case, not by eye.
 *
 * The consumer branch renders 1 100 bytes with every counter at its
 * widest: the stats object alone is 345, and the playout object has grown
 * a receive-buffer size, two fill marks, three deadline counters, two
 * margins and five distribution buckets since this was 832.
 *
 * It did not fail on the first packet, which is what made it confusing.
 * The numbers widen with uptime -- packetsSubmitted, framesPlayed and the
 * buckets all gain digits -- so a node played correctly for minutes and
 * then started answering 500, and the Hub reported it as a node that had
 * stopped talking rather than as a response that no longer fitted.
 *
 * Measured by extracting the snprintf onto the host and rendering it with
 * every field saturated. Re-measure before adding another field; this is
 * the third time a fixed buffer or format string in this file has cost a
 * debugging session.
 */
static char s_stream_body[1536];

static esp_err_t stream_get_handler(httpd_req_t *req)
{
    char *body = s_stream_body;
    const size_t body_size = sizeof(s_stream_body);
    int len;

    if ((s_config.roles & OAL_ROLE_PRODUCER) != 0) {
        oal_stream_producer_state_t p;
        oal_stream_producer_get(&p);
        /*
         * The addresses, not just how many. A Controller correcting a
         * stream after a speaker rejoined on a different address has to be
         * able to see that the stream is pointed at the old one — and a
         * count is identical whether it is right or wrong, which makes the
         * failure look like perfect health.
         */
        char destinations[OAL_STREAM_MAX_DESTINATIONS * (OAL_ADDRESS_MAX + 3) + 4] = "[]";
        {
            oal_destinations_t set;
            oal_stream_producer_destinations(&set);
            size_t w = 0;
            destinations[w++] = '[';
            for (size_t i = 0; i < set.count && w + 20 < sizeof(destinations); i++) {
                w += (size_t)snprintf(destinations + w, sizeof(destinations) - w,
                                      "%s\"%s\"", i ? "," : "", set.entries[i]);
            }
            if (w + 2 < sizeof(destinations)) {
                destinations[w++] = ']';
                destinations[w] = '\0';
            }
        }

        len = snprintf(body, body_size,
                       "{\"role\":\"producer\",\"running\":%s,\"port\":%u,"
                       "\"destinations\":%u,\"destinationList\":%s,\"source\":\"%s\","
                       "\"packetsSent\":%u,\"datagramsSent\":%u,"
                       "\"sendErrors\":%u,\"sendRetries\":%u,\"lastSendErrno\":%d,"
                       "\"latePackets\":%u}",
                       p.running ? "true" : "false", p.port,
                       (unsigned)p.destination_count, destinations,
                       p.source == OAL_RTP_SOURCE_TONE ? "tone"
                           : p.source == OAL_RTP_SOURCE_CAPTURE ? "capture" : "pattern",
                       (unsigned)p.packets_sent, (unsigned)p.datagrams_sent,
                       (unsigned)p.send_errors, (unsigned)p.send_retries,
                       p.last_send_errno, (unsigned)p.late_packets);
    } else {
        oal_stream_consumer_state_t c;
        oal_stream_consumer_get(&c);
        char stats[OAL_RTP_STATS_JSON_MAX];
        if (oal_rtp_stats_to_json(&c.stats, stats, sizeof(stats)) < 0) {
            httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "stats too large");
            return ESP_FAIL;
        }
        /*
         * The playout counters travel with the reception statistics
         * because they answer the same question from the other side. Loss
         * on the air and a ring that ran dry both sound like a click, and
         * only having both numbers says which happened.
         */
        oal_playout_state_t audio;
        oal_playout_get(&audio);

        len = snprintf(body, body_size,
                       "{\"role\":\"consumer\",\"listening\":%s,\"port\":%u,"
                       "\"rxBufferBytes\":%d,"
                       "\"payloadErrors\":%u,\"foreignPackets\":%u,"
                       "\"lastSsrc\":\"%08x\","
                       "\"playout\":{\"running\":%s,\"playing\":%s,\"channel\":\"%s\","
                       "\"volume\":%u,"
                       "\"bufferedFrames\":%u,\"targetFrames\":%u,"
                       "\"silenceFrames\":%u,\"droppedFrames\":%u,"
                       "\"underruns\":%u,\"trimmedFrames\":%u,"
                       "\"paddedFrames\":%u,"
                       /* The three that answer "are these two speakers
                        * together". Depth is the whole of sync here --
                        * nothing says when a sample is due -- so two nodes
                        * agree when their fills match, and steerFrames is
                        * the line both aim at. primeDiscardedFrames is the
                        * burst thrown away at the last prime, which is what
                        * used to become a permanent offset. */
                       "\"primedFrames\":%u,\"primeDiscardedFrames\":%u,"
                       "\"steerFrames\":%u,"
                       /*
                        * The sample this speaker is playing, on the
                        * sender's timeline, and whether it means anything
                        * yet.
                        *
                        * The one figure that answers "are these two
                        * together" without inference. Depth was the proxy
                        * and it conflates arrival with playback: a burst
                        * raises it without the sound moving, which is why
                        * it swings a hundred milliseconds in seconds while
                        * two speakers stay in step.
                        *
                        * Comparable across nodes with no clock agreement,
                        * because RTP timestamps come from the single
                        * sender. It wraps at 2^32, about a day at 48 kHz;
                        * the reader subtracts in unsigned arithmetic and
                        * the wrap takes care of itself.
                        */
                       "\"playingTimestamp\":%" PRIu32 ",\"playingKnown\":%s,"
                       /* The low- and high-water marks of the last trace
                        * window. bufferedFrames is one instant, and a poll
                        * every fifteen seconds samples one moment in three
                        * thousand -- a ring that reads healthy on every
                        * poll can still be touching zero between them. */
                       "\"fillMinFrames\":%u,\"fillMaxFrames\":%u,"
                       /* Whether packets made their deadline, which is the
                        * only question that decides what a listener hears.
                        * latePackets means the ring was already dry when
                        * the payload arrived; tightPackets is the warning
                        * population that has not cost anything yet. */
                       "\"packetsSubmitted\":%llu,\"latePackets\":%u,"
                       "\"tightPackets\":%u,\"marginMinFrames\":%u,"
                       "\"marginWorstFrames\":%u,"
                       /* Where the packets actually landed, as fractions
                        * of the target cushion: under 10%, 10-25, 25-50,
                        * 50-75, 75 and over. A minimum is one draw; this
                        * is the shape. */
                       "\"marginBuckets\":[%u,%u,%u,%u,%u],"
                       "\"framesPlayed\":%llu,\"writeErrors\":%u,\"resyncs\":%u},"
                       "\"stats\":%s}",
                       c.listening ? "true" : "false", c.port,
                       c.rx_buffer_bytes,
                       (unsigned)c.payload_errors, (unsigned)c.foreign_packets,
                       (unsigned)c.last_ssrc,
                       audio.running ? "true" : "false",
                       audio.playing ? "true" : "false",
                       oal_channel_name(audio.channel),
                       (unsigned)audio.volume,
                       (unsigned)audio.buffered_frames, (unsigned)audio.target_frames,
                       (unsigned)audio.silence_frames, (unsigned)audio.dropped_frames,
                       (unsigned)audio.underruns, (unsigned)audio.trimmed_frames,
                       (unsigned)audio.padded_frames,
                       (unsigned)audio.primed_frames,
                       (unsigned)audio.prime_discarded_frames,
                       (unsigned)audio.steer_frames,
                       /* Newest accepted, less what is still waiting. Both
                        * read within microseconds of each other here, which
                        * is far inside one frame. */
                       c.newest_timestamp - (uint32_t)audio.buffered_frames,
                       (c.have_newest && audio.playing) ? "true" : "false",
                       (unsigned)audio.fill_min_frames, (unsigned)audio.fill_max_frames,
                       (unsigned long long)audio.packets_submitted,
                       (unsigned)audio.late_packets, (unsigned)audio.tight_packets,
                       (unsigned)audio.margin_min_frames,
                       (unsigned)audio.margin_worst_frames,
                       (unsigned)audio.margin_buckets[0], (unsigned)audio.margin_buckets[1],
                       (unsigned)audio.margin_buckets[2], (unsigned)audio.margin_buckets[3],
                       (unsigned)audio.margin_buckets[4],
                       (unsigned long long)audio.frames_played,
                       (unsigned)audio.write_errors,
                       (unsigned)audio.resyncs,
                       stats);
    }

    if (len <= 0 || len >= (int)body_size) {
        /* Loud, because from outside this is indistinguishable from a
         * node that has stopped answering. */
        ESP_LOGE(TAG, "stream status needs %d bytes, buffer is %u",
                 len, (unsigned)body_size);
        httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "stream status too large");
        return ESP_FAIL;
    }
    httpd_resp_set_type(req, "application/json");
    return httpd_resp_send(req, body, len);
}

/*
 * Which radios can this node actually see, and which is it on.
 *
 * The question that had no answer until now. `/status` reports the access
 * point the node landed on and nothing about the alternatives, so in a mesh
 * — one SSID, several radios — "why is it on that one" could only be
 * investigated by carrying the board around the house, and once by cupping
 * a hand over the antenna to make a scan come out differently. This is that
 * investigation, as a request.
 *
 * GET rather than POST despite costing a second of connection, because it
 * changes nothing. It is still not something to poll: every packet due
 * while the radio is scanning is lost, and on a playing node that is
 * audible.
 */
static esp_err_t wifi_scan_handler(httpd_req_t *req)
{
    static char scan[OAL_WIFI_SCAN_JSON_MAX];

    int len = oal_wifi_scan_json(scan, sizeof(scan));
    if (len < 0) {
        httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "scan failed");
        return ESP_FAIL;
    }
    httpd_resp_set_type(req, "application/json");
    return httpd_resp_send(req, scan, len);
}

/*
 * Forget the current access point and join again from a fresh scan.
 *
 * Measured need: a node carried to within a metre of one access point kept
 * rejoining one twenty metres away, through reboots and a power cycle. The
 * sticky rule is right for a speaker on a shelf and cannot know about a
 * board that has just moved house.
 *
 * This does not override the selection rule, and deliberately: it clears
 * the memory the rule is sticky about and asks for a scan. Landing on the
 * same access point is a legitimate outcome and worth knowing.
 */
/*
 * Deferred, for the reason `restart_task` is: this request is answered
 * over the association it is about to drop.
 *
 * The first version called `oal_wifi_rejoin()` — which disconnects — and
 * only then sent the response. The response then had no link to leave by,
 * so the Hub saw the request fail and reported "Rejoin request failed"
 * while the node went ahead and rejoined perfectly. Worse than a plain
 * bug: it told the operator the opposite of what happened, on the one
 * screen built to say what happened.
 *
 * It was a race rather than a certainty, which is why it ever looked like
 * it worked — on an idle node the bytes sometimes reached the wire first.
 * The same 500 ms reboot uses is far longer than that needs.
 */
static void rejoin_task(void *arg)
{
    vTaskDelay(pdMS_TO_TICKS(500));
    (void)oal_wifi_rejoin();
    vTaskDelete(NULL);
}

static esp_err_t wifi_rejoin_handler(httpd_req_t *req)
{
    ESP_LOGW(TAG, "rejoin requested");
    httpd_resp_set_type(req, "application/json");
    esp_err_t sent = httpd_resp_send(req,
        "{\"status\":\"rejoining\",\"note\":\"read /status in a few seconds for the bssid\"}",
        HTTPD_RESP_USE_STRLEN);

    /* Only after the answer is on its way, and only if it got there. A
     * node that drops its link for a request nobody received has done the
     * disruptive half of the job and none of the useful half. */
    if (sent == ESP_OK) {
        xTaskCreate(rejoin_task, "oal_rejoin", 3072, NULL, 5, NULL);
    }
    return sent;
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
        if (!cJSON_IsString(element)) {
            cJSON_Delete(root);
            httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "bad destinations");
            return ESP_FAIL;
        }
        /* An address that is not a dotted quad becomes INADDR_NONE, which
         * is the broadcast address — a typo would aim the stream at the
         * whole network rather than fail. */
        oal_destinations_result_t added =
            oal_destinations_add(&request.destinations, element->valuestring);
        if (added == OAL_DEST_INVALID || added == OAL_DEST_FULL) {
            cJSON_Delete(root);
            httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST,
                                added == OAL_DEST_FULL ? "too many destinations"
                                                       : "destination is not an IPv4 address");
            return ESP_FAIL;
        }
    }

    const cJSON *port = cJSON_GetObjectItemCaseSensitive(root, "port");
    const cJSON *source = cJSON_GetObjectItemCaseSensitive(root, "source");
    const cJSON *tone = cJSON_GetObjectItemCaseSensitive(root, "toneHz");
    request.port = cJSON_IsNumber(port) ? (uint16_t)port->valueint : OAL_RTP_DEFAULT_PORT;
    /*
     * "capture" is accepted whether or not an ADC is attached. A producer
     * asked for real audio with nothing wired sends timed silence, which
     * keeps receivers primed and makes attaching the input a matter of
     * plugging it in rather than of restarting the stream.
     */
    request.source = OAL_RTP_SOURCE_PATTERN;
    if (cJSON_IsString(source)) {
        if (strcmp(source->valuestring, "tone") == 0) {
            request.source = OAL_RTP_SOURCE_TONE;
        } else if (strcmp(source->valuestring, "capture") == 0) {
            request.source = OAL_RTP_SOURCE_CAPTURE;
        }
    }
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

/* ---------- POST /stream/destinations ---------- */

/*
 * Adds and removes destinations while a stream runs, so a Consumer that
 * asks to join gets audio without the record being restarted for everyone
 * already listening (decision 9).
 *
 * Both lists in one request because a Controller moving a speaker from one
 * room to another wants the removal and the addition to happen together,
 * and because doing it in two requests leaves a moment where the speaker
 * is in neither.
 */
static void apply_destination_list(const cJSON *list, bool add,
                                   unsigned *changed, unsigned *rejected)
{
    if (!cJSON_IsArray(list)) {
        return;
    }
    const cJSON *element = NULL;
    cJSON_ArrayForEach(element, list) {
        if (!cJSON_IsString(element) || element->valuestring == NULL) {
            (*rejected)++;
            continue;
        }
        if (add) {
            if (oal_stream_producer_add_destination(element->valuestring) == OAL_DEST_ADDED) {
                (*changed)++;
            } else if (!oal_address_is_ipv4(element->valuestring)) {
                (*rejected)++;
            }
        } else if (oal_stream_producer_remove_destination(element->valuestring)) {
            (*changed)++;
        }
    }
}

static esp_err_t destinations_handler(httpd_req_t *req)
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
    if (root == NULL) {
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "bad json");
        return ESP_FAIL;
    }

    unsigned changed = 0;
    unsigned rejected = 0;
    /* Removals first: moving a speaker between rooms must not be refused
     * for filling the set with an entry that is on its way out. */
    apply_destination_list(cJSON_GetObjectItemCaseSensitive(root, "remove"),
                           false, &changed, &rejected);
    apply_destination_list(cJSON_GetObjectItemCaseSensitive(root, "add"),
                           true, &changed, &rejected);
    cJSON_Delete(root);

    oal_destinations_t current;
    oal_stream_producer_destinations(&current);

    httpd_resp_set_type(req, "application/json");
    httpd_resp_send_chunk(req, "{\"destinations\":[", HTTPD_RESP_USE_STRLEN);
    for (size_t i = 0; i < current.count; i++) {
        char entry[OAL_ADDRESS_MAX + 4];
        int n = snprintf(entry, sizeof(entry), "%s\"%s\"",
                         i == 0 ? "" : ",", current.entries[i]);
        if (n > 0 && n < (int)sizeof(entry)) {
            httpd_resp_send_chunk(req, entry, n);
        }
    }
    char tail[64];
    int n = snprintf(tail, sizeof(tail), "],\"changed\":%u,\"rejected\":%u}",
                     changed, rejected);
    httpd_resp_send_chunk(req, tail, n);
    return httpd_resp_send_chunk(req, NULL, 0);
}

/* ---------- POST /join ---------- */

/*
 * A Consumer saying it is ready (decision 9). The Consumer initiates and
 * this decides.
 *
 * What is answered depends on what this node is. A Controller that also
 * produces adds the caller to its destinations — and because the two roles
 * are on the same node, that is a function call rather than a request over
 * the air, which is the whole reason the party system has no control plane
 * to fail.
 */
/*
 * Where to send, for a node that asked to be sent to.
 *
 * The connection's own address first: it cannot be forged by asking, and
 * it is right even for a node whose announcement has not arrived yet. When
 * the socket is not plain IPv4 — dual-stack builds hand back a mapped
 * address — fall back to the id in the request and look it up in the peer
 * table, where the address came from the announcement's source rather than
 * from anything the caller wrote.
 */
static void join_address(httpd_req_t *req, const char *body, char *out, size_t out_size)
{
    out[0] = '\0';

    struct sockaddr_storage peer;
    socklen_t peer_len = sizeof(peer);
    if (getpeername(httpd_req_to_sockfd(req), (struct sockaddr *)&peer, &peer_len) == 0
            && peer.ss_family == AF_INET) {
        struct sockaddr_in *v4 = (struct sockaddr_in *)&peer;
        inet_ntoa_r(v4->sin_addr, out, (int)out_size);
        return;
    }

    cJSON *root = cJSON_ParseWithLength(body, strlen(body));
    const cJSON *id = root != NULL
        ? cJSON_GetObjectItemCaseSensitive(root, "id") : NULL;
    if (cJSON_IsString(id) && id->valuestring != NULL) {
        static oal_peer_t peers[OAL_MAX_PEERS];
        size_t count = oal_discovery_peers(peers, OAL_MAX_PEERS);
        for (size_t i = 0; i < count; i++) {
            if (strcmp(peers[i].id, id->valuestring) == 0) {
                snprintf(out, out_size, "%s", peers[i].address);
                break;
            }
        }
    }
    cJSON_Delete(root);
}

static esp_err_t join_handler(httpd_req_t *req)
{
    char body[128];
    int len = httpd_req_recv(req, body, sizeof(body) - 1);
    if (len <= 0) {
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "empty body");
        return ESP_FAIL;
    }
    body[len] = '\0';

    if (oal_discovery_controller(NULL) != OAL_CONTROLLER_SELF) {
        /* Not ours to answer. The caller runs the same election we do and
         * will re-target on its next round, so this is a moment during a
         * handover rather than an error to act on. */
        httpd_resp_set_status(req, "409 Conflict");
        httpd_resp_set_type(req, "application/json");
        return httpd_resp_send(req, "{\"status\":\"notController\"}", HTTPD_RESP_USE_STRLEN);
    }

    char address[OAL_ADDRESS_MAX] = { 0 };
    join_address(req, body, address, sizeof(address));

    const char *status = "standby";
    if (address[0] != '\0' && (s_config.roles & OAL_ROLE_PRODUCER) != 0) {
        oal_destinations_result_t added = oal_stream_producer_add_destination(address);
        if (added == OAL_DEST_ADDED) {
            ESP_LOGI(TAG, "%s joined; now streaming to it too", address);
        }
        if (added == OAL_DEST_ADDED || added == OAL_DEST_ALREADY_PRESENT) {
            oal_stream_producer_state_t producer;
            oal_stream_producer_get(&producer);
            /* Honest about what is happening rather than about what was
             * asked for: a destination on a Producer with nothing to send
             * is standing by, not playing. */
            status = producer.running ? "playing" : "standby";
        }
    }

    char response[64];
    int n = snprintf(response, sizeof(response), "{\"status\":\"%s\"}", status);
    httpd_resp_set_type(req, "application/json");
    return httpd_resp_send(req, response, n);
}

/* ---------- GET /peers ---------- */

/*
 * What this node has heard from other nodes (decision 9). Nothing acts on
 * it yet, so this endpoint is the whole of its observable behaviour — and
 * a peer table that cannot be read is one that cannot be shown to be
 * working before anything is built on top of it.
 *
 * Sent in chunks rather than assembled in one buffer: sixteen peers would
 * be some two kilobytes on a task stack that also has to serve OTA.
 */
static esp_err_t peers_handler(httpd_req_t *req)
{
    static oal_peer_t peers[OAL_MAX_PEERS];
    size_t count = oal_discovery_peers(peers, OAL_MAX_PEERS);
    const int64_t now = esp_timer_get_time();

    httpd_resp_set_type(req, "application/json");
    httpd_resp_send_chunk(req, "{\"peers\":[", HTTPD_RESP_USE_STRLEN);

    for (size_t i = 0; i < count; i++) {
        char roles[OAL_ROLES_STR_MAX];
        if (oal_roles_to_json(peers[i].roles, roles, sizeof(roles)) < 0) {
            snprintf(roles, sizeof(roles), "[]");
        }

        char entry[256];
        int len = snprintf(entry, sizeof(entry),
                           "%s{\"id\":\"%s\",\"name\":\"%s\",\"roles\":%s,"
                           "\"address\":\"%s\",\"ctrlPort\":%u,\"ageMs\":%lld}",
                           i == 0 ? "" : ",",
                           peers[i].id, peers[i].name, roles,
                           peers[i].address, (unsigned)peers[i].control_port,
                           (long long)((now - peers[i].last_seen_us) / 1000));
        if (len > 0 && len < (int)sizeof(entry)) {
            httpd_resp_send_chunk(req, entry, len);
        }
    }

    httpd_resp_send_chunk(req, "]}", HTTPD_RESP_USE_STRLEN);
    return httpd_resp_send_chunk(req, NULL, 0);
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
    /* `name` is an array now, not a pointer, so the old NULL test against it
     * could never fire — which the compiler says out loud under -Werror=address.
     * Empty is the failure that actually matters: a node announcing a blank
     * name is one no list can show. */
    if (config == NULL || config->id == NULL || config->name[0] == '\0'
        || config->roles == OAL_ROLE_NONE
        || config->hardware_profile == NULL || config->firmware_version == NULL) {
        return ESP_ERR_INVALID_ARG;
    }
    s_config = *config;

    httpd_config_t server_config = HTTPD_DEFAULT_CONFIG();
    server_config.server_port = CONTROL_PORT;
    /* Thirteen registered, and the default of eight silently refuses the
     * rest — a handler that never registers answers 404 and looks like a
     * firmware that predates the endpoint. Sized with room to spare so the
     * next endpoint does not have to remember this. */
    server_config.max_uri_handlers = 16;

    /*
     * Belt as well as braces, after the handlers stopped putting their
     * response buffers here.
     *
     * The default is 4096, and one stack carries the request parser, the
     * handler and the reply — plus an interrupt frame if one lands at the
     * deepest moment. That is what overflowed on the vinyl node. Moving
     * ~2.4 kB of buffers to .bss is the fix; this is the margin, because
     * the thing that overflows a stack is rarely the thing you measured.
     *
     * It costs 2 kB of RAM once, against a reboot mid-record. /status now
     * reports httpdStackFreeB so this number can be checked rather than
     * believed.
     */
    server_config.stack_size = 6144;

    /*
     * Close the oldest connection rather than refusing the newest.
     *
     * The default is to refuse, and a node that refuses is a node that has
     * disappeared: the Hub polls it, the portal will not load, and OTA
     * cannot start. It happened on hardware — `httpd_accept_conn: error in
     * accept (23)`, lwIP out of sockets, repeating forever — because
     * nothing here ever gets to decide when a client goes away. A caller
     * that opens a connection and leaves it idle costs one of the seven
     * slots until it feels like closing, and two Hubs polling, a browser
     * tab left open, or a client that keeps its connection alive is enough
     * to take them all.
     *
     * Dropping the least recently used one is the right trade for a device
     * whose requests are all short: the worst case is one client having to
     * reconnect, against the whole control plane going dark.
     */
    server_config.lru_purge_enable = true;

    httpd_handle_t server = NULL;
    esp_err_t err = httpd_start(&server, &server_config);
    if (err != ESP_OK) {
        return err;
    }

    httpd_uri_t status = { .uri = "/status", .method = HTTP_GET, .handler = status_handler };
    httpd_uri_t reboot = { .uri = "/reboot", .method = HTTP_POST, .handler = reboot_handler };
    httpd_uri_t ota = { .uri = "/ota", .method = HTTP_POST, .handler = ota_handler };
    httpd_uri_t set_config = { .uri = "/config", .method = HTTP_POST, .handler = config_handler };
    httpd_uri_t volume = { .uri = "/volume", .method = HTTP_POST, .handler = volume_handler };
    httpd_uri_t stream = { .uri = "/stream", .method = HTTP_GET, .handler = stream_get_handler };
    httpd_uri_t stream_start =
        { .uri = "/stream/start", .method = HTTP_POST, .handler = stream_start_handler };
    httpd_uri_t stream_stop =
        { .uri = "/stream/stop", .method = HTTP_POST, .handler = stream_stop_handler };
    httpd_uri_t peers = { .uri = "/peers", .method = HTTP_GET, .handler = peers_handler };
    httpd_uri_t root = { .uri = "/", .method = HTTP_GET, .handler = root_handler };
    httpd_uri_t wifi_scan =
        { .uri = "/wifi/scan", .method = HTTP_GET, .handler = wifi_scan_handler };
    httpd_uri_t wifi_rejoin =
        { .uri = "/wifi/rejoin", .method = HTTP_POST, .handler = wifi_rejoin_handler };
    httpd_uri_t join = { .uri = "/join", .method = HTTP_POST, .handler = join_handler };
    httpd_uri_t destinations =
        { .uri = "/stream/destinations", .method = HTTP_POST, .handler = destinations_handler };
    httpd_register_uri_handler(server, &root);
    httpd_register_uri_handler(server, &wifi_scan);
    httpd_register_uri_handler(server, &wifi_rejoin);
    httpd_register_uri_handler(server, &status);
    httpd_register_uri_handler(server, &reboot);
    httpd_register_uri_handler(server, &ota);
    httpd_register_uri_handler(server, &set_config);
    httpd_register_uri_handler(server, &volume);
    httpd_register_uri_handler(server, &stream);
    httpd_register_uri_handler(server, &stream_start);
    httpd_register_uri_handler(server, &stream_stop);
    httpd_register_uri_handler(server, &peers);
    httpd_register_uri_handler(server, &join);
    httpd_register_uri_handler(server, &destinations);

    ESP_LOGI(TAG, "control server on port %d", CONTROL_PORT);
    return ESP_OK;
}
