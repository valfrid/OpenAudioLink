#include "oal_control.h"

#include <stdbool.h>
#include <stdlib.h>
#include <string.h>

#include "cJSON.h"
#include "oal_discovery.h"
#include "oal_join.h"
#include "oal_playout.h"
#include "oal_stream.h"
#include "esp_http_server.h"
#include "lwip/sockets.h"
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

    char controller[160];
    if (format_controller(controller, sizeof(controller)) >= (int)sizeof(controller)) {
        snprintf(controller, sizeof(controller), "{\"who\":\"none\"}");
    }

    /* Only a Consumer joins, so only a Consumer has anything to report
     * about it. A producer saying "not asked" would read as a failure. */
    char join[96] = "null";
    if ((s_config.roles & OAL_ROLE_CONSUMER) != 0) {
        snprintf(join, sizeof(join), "{\"asked\":%s,\"status\":\"%s\"}",
                 oal_join_acknowledged() ? "true" : "false", oal_join_last_status());
    }

    char body[896];
    int len = snprintf(body, sizeof(body),
                       "{\"oal\":\"" PROTOCOL_VERSION "\",\"id\":\"%s\",\"name\":\"%s\","
                       "\"roles\":%s,\"channel\":\"%s\",\"hw\":\"%s\",\"fw\":\"%s\","
                       "\"uptimeS\":%lld,\"heapFree\":%u,\"wifi\":%s,"
                       "\"controller\":%s,\"join\":%s,"
                       "\"audio\":{\"state\":\"idle\"}}",
                       s_config.id, s_config.name, roles,
                       oal_channel_name(oal_config_get_channel()),
                       s_config.hardware_profile, s_config.firmware_version,
                       (long long)(esp_timer_get_time() / 1000000),
                       (unsigned)esp_get_free_heap_size(), wifi,
                       controller, join);
    if (len <= 0 || len >= (int)sizeof(body)) {
        httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, "status too large");
        return ESP_FAIL;
    }

    httpd_resp_set_type(req, "application/json");
    return httpd_resp_send(req, body, len);
}

/* ---------- POST /config ---------- */

/*
 * Sets what this node is and what it plays:
 * {"roles":["consumer"],"channel":"mono"}. Either field alone is valid.
 *
 * Stored in NVS and applied at the next boot. Roles decide which tasks
 * start, and the channel decides what the playout does with each frame;
 * changing either under a running stream would mean tearing down live
 * audio, and a reboot is both simpler and more honest about what happened.
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
    if (root == NULL) {
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "bad json");
        return ESP_FAIL;
    }

    const cJSON *array = cJSON_GetObjectItemCaseSensitive(root, "roles");
    const cJSON *channel = cJSON_GetObjectItemCaseSensitive(root, "channel");

    /* Which fields were present, recorded now. Everything below happens
     * after the tree is freed, and testing a pointer into a freed tree is
     * the kind of bug that works until the allocator is under pressure. */
    const bool has_roles = cJSON_IsArray(array);
    const bool has_channel = cJSON_IsString(channel);

    /* Either may be set alone: changing a speaker from stereo to mono has
     * nothing to do with whether it is still a consumer, and requiring
     * both would make one setting able to clobber the other. */
    if (!has_roles && !has_channel) {
        cJSON_Delete(root);
        httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "expected roles or channel");
        return ESP_FAIL;
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

    char stored[OAL_ROLES_STR_MAX];
    char response[192];
    oal_roles_to_json(oal_config_get_roles(), stored, sizeof(stored));
    int n = snprintf(response, sizeof(response),
                     "{\"status\":\"stored\",\"roles\":%s,\"channel\":\"%s\","
                     "\"appliesAt\":\"reboot\"}",
                     stored, oal_channel_name(oal_config_get_channel()));
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
    char body[832];
    int len;

    if ((s_config.roles & OAL_ROLE_PRODUCER) != 0) {
        oal_stream_producer_state_t p;
        oal_stream_producer_get(&p);
        len = snprintf(body, sizeof(body),
                       "{\"role\":\"producer\",\"running\":%s,\"port\":%u,"
                       "\"destinations\":%u,\"source\":\"%s\","
                       "\"packetsSent\":%u,\"datagramsSent\":%u,"
                       "\"sendErrors\":%u,\"sendRetries\":%u,\"lastSendErrno\":%d,"
                       "\"latePackets\":%u}",
                       p.running ? "true" : "false", p.port,
                       (unsigned)p.destination_count,
                       p.source == OAL_RTP_SOURCE_TONE ? "tone" : "pattern",
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

        len = snprintf(body, sizeof(body),
                       "{\"role\":\"consumer\",\"listening\":%s,\"port\":%u,"
                       "\"payloadErrors\":%u,\"foreignPackets\":%u,"
                       "\"lastSsrc\":\"%08x\","
                       "\"playout\":{\"running\":%s,\"playing\":%s,\"channel\":\"%s\","
                       "\"bufferedFrames\":%u,\"targetFrames\":%u,"
                       "\"silenceFrames\":%u,\"droppedFrames\":%u,"
                       "\"framesPlayed\":%llu,\"writeErrors\":%u},"
                       "\"stats\":%s}",
                       c.listening ? "true" : "false", c.port,
                       (unsigned)c.payload_errors, (unsigned)c.foreign_packets,
                       (unsigned)c.last_ssrc,
                       audio.running ? "true" : "false",
                       audio.playing ? "true" : "false",
                       oal_channel_name(audio.channel),
                       (unsigned)audio.buffered_frames, (unsigned)audio.target_frames,
                       (unsigned)audio.silence_frames, (unsigned)audio.dropped_frames,
                       (unsigned long long)audio.frames_played,
                       (unsigned)audio.write_errors,
                       stats);
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
    if (config == NULL || config->id == NULL || config->name == NULL
        || config->roles == OAL_ROLE_NONE
        || config->hardware_profile == NULL || config->firmware_version == NULL) {
        return ESP_ERR_INVALID_ARG;
    }
    s_config = *config;

    httpd_config_t server_config = HTTPD_DEFAULT_CONFIG();
    server_config.server_port = CONTROL_PORT;
    server_config.max_uri_handlers = 12; /* ten registered; the default of eight leaves no room */

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
    httpd_uri_t peers = { .uri = "/peers", .method = HTTP_GET, .handler = peers_handler };
    httpd_uri_t join = { .uri = "/join", .method = HTTP_POST, .handler = join_handler };
    httpd_uri_t destinations =
        { .uri = "/stream/destinations", .method = HTTP_POST, .handler = destinations_handler };
    httpd_register_uri_handler(server, &status);
    httpd_register_uri_handler(server, &reboot);
    httpd_register_uri_handler(server, &ota);
    httpd_register_uri_handler(server, &set_config);
    httpd_register_uri_handler(server, &stream);
    httpd_register_uri_handler(server, &stream_start);
    httpd_register_uri_handler(server, &stream_stop);
    httpd_register_uri_handler(server, &peers);
    httpd_register_uri_handler(server, &join);
    httpd_register_uri_handler(server, &destinations);

    ESP_LOGI(TAG, "control server on port %d", CONTROL_PORT);
    return ESP_OK;
}
