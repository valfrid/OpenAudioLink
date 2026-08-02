#include "oal_discovery.h"

#include <string.h>

#include "cJSON.h"
#include "esp_log.h"
#include "esp_random.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "lwip/sockets.h"

static const char *TAG = "oal_discovery";

static oal_discovery_config_t s_config;
static char s_announce[256];
static int s_announce_len;

/*
 * Controller state (decision 9). Held under the peer table's lock because
 * it is derived from the table and read by the control server.
 */
static oal_controller_who_t s_controller;
static oal_peer_t s_controller_peer;
static bool s_claimed;

/*
 * A claimed role is announced so other nodes can rank it below a Hub. The
 * field is absent rather than false when not claiming, so an older node
 * reading this announcement sees exactly what it saw before.
 */
static int build_announce(void)
{
    char roles[OAL_ROLES_STR_MAX];
    oal_roles_t announced = s_config.roles | (s_claimed ? OAL_ROLE_CONTROLLER : 0u);
    if (oal_roles_to_json(announced, roles, sizeof(roles)) < 0) {
        return -1;
    }

    return snprintf(s_announce, sizeof(s_announce),
                    "{\"oal\":\"" OAL_PROTOCOL_VERSION "\",\"type\":\"announce\","
                    "\"id\":\"%s\",\"name\":\"%s\",\"roles\":%s,"
                    "\"hw\":\"%s\",\"fw\":\"%s\",\"ctrlPort\":%d%s}",
                    s_config.id, s_config.name, roles,
                    s_config.hardware_profile, s_config.firmware_version,
                    OAL_DEVICE_CONTROL_PORT,
                    s_claimed ? ",\"claimed\":true" : "");
}

/*
 * The table itself is in oal_peers.c and knows nothing about tasks; this
 * owns the lock around it. Written only by the discovery task and read by
 * the control server, so a mutex rather than an atomic: a reader must not
 * copy a record halfway through being overwritten by a re-announce.
 */
static oal_peer_table_t s_peers;
static SemaphoreHandle_t s_peers_lock;

static void copy_field(char *dst, size_t dst_size, const cJSON *item)
{
    if (cJSON_IsString(item) && item->valuestring != NULL) {
        snprintf(dst, dst_size, "%s", item->valuestring);
    } else {
        dst[0] = '\0';
    }
}

static void remember_peer(const cJSON *root, const struct sockaddr_in *from)
{
    const cJSON *id = cJSON_GetObjectItemCaseSensitive(root, "id");
    if (!cJSON_IsString(id) || id->valuestring == NULL || id->valuestring[0] == '\0') {
        return;
    }

    /* Our own announcements come back to us: joining the group enables
     * loopback by default, and a node listing itself as a peer would make
     * every count off by one. */
    if (strcmp(id->valuestring, s_config.id) == 0) {
        return;
    }

    oal_peer_t peer = { 0 };
    copy_field(peer.id, sizeof(peer.id), id);
    copy_field(peer.name, sizeof(peer.name),
               cJSON_GetObjectItemCaseSensitive(root, "name"));

    const cJSON *roles = cJSON_GetObjectItemCaseSensitive(root, "roles");
    if (cJSON_IsArray(roles)) {
        const cJSON *entry = NULL;
        cJSON_ArrayForEach(entry, roles) {
            if (cJSON_IsString(entry) && entry->valuestring != NULL) {
                peer.roles |= oal_role_from_name(entry->valuestring);
            }
        }
    }

    const cJSON *port = cJSON_GetObjectItemCaseSensitive(root, "ctrlPort");
    peer.control_port = cJSON_IsNumber(port)
        ? (uint16_t)port->valueint : OAL_DEVICE_CONTROL_PORT;
    peer.claimed = cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(root, "claimed"));
    inet_ntoa_r(from->sin_addr, peer.address, (int)sizeof(peer.address));
    peer.last_seen_us = esp_timer_get_time();

    if (xSemaphoreTake(s_peers_lock, pdMS_TO_TICKS(100)) != pdTRUE) {
        return;
    }
    if (oal_peer_table_record(&s_peers, &peer)) {
        ESP_LOGI(TAG, "peer %s (%s) at %s", peer.name, peer.id, peer.address);
    }
    xSemaphoreGive(s_peers_lock);
}

size_t oal_discovery_peers(oal_peer_t *out, size_t max)
{
    if (s_peers_lock == NULL
        || xSemaphoreTake(s_peers_lock, pdMS_TO_TICKS(100)) != pdTRUE) {
        return 0;
    }
    size_t written = oal_peer_table_live(&s_peers, esp_timer_get_time(), out, max);
    xSemaphoreGive(s_peers_lock);
    return written;
}

oal_controller_who_t oal_discovery_controller(oal_peer_t *out)
{
    if (s_peers_lock == NULL
        || xSemaphoreTake(s_peers_lock, pdMS_TO_TICKS(100)) != pdTRUE) {
        return OAL_CONTROLLER_UNKNOWN;
    }
    oal_controller_who_t who = s_controller;
    if (out != NULL && who == OAL_CONTROLLER_PEER) {
        *out = s_controller_peer;
    }
    xSemaphoreGive(s_peers_lock);
    return who;
}

/*
 * Recomputed from the live peers every announce interval. Cheap — at most
 * sixteen string compares — and doing it repeatedly rather than on an event
 * means a node that misses an announcement corrects itself on the next one
 * instead of staying wrong.
 */
static void run_election(void)
{
    static oal_peer_t live[OAL_MAX_PEERS];
    static oal_election_peer_t candidates[OAL_MAX_PEERS];

    if (xSemaphoreTake(s_peers_lock, pdMS_TO_TICKS(100)) != pdTRUE) {
        return;
    }

    size_t count = oal_peer_table_live(&s_peers, esp_timer_get_time(), live, OAL_MAX_PEERS);
    for (size_t i = 0; i < count; i++) {
        candidates[i].id = live[i].id;
        candidates[i].roles = live[i].roles;
        candidates[i].claimed = live[i].claimed;
    }

    size_t winner = 0;
    oal_election_result_t result =
        oal_election_run(s_config.id, s_config.roles, candidates, count, &winner);

    oal_controller_who_t was = s_controller;
    bool was_claimed = s_claimed;

    switch (result) {
    case OAL_ELECTION_SELF:
        s_controller = OAL_CONTROLLER_SELF;
        /* Claimed only when the role was not configured. The Hub holds it
         * by configuration and must never announce itself as a claimer. */
        s_claimed = (s_config.roles & OAL_ROLE_CONTROLLER) == 0;
        break;
    case OAL_ELECTION_PEER:
        s_controller = OAL_CONTROLLER_PEER;
        s_controller_peer = live[winner];
        s_claimed = false;
        break;
    default:
        s_controller = OAL_CONTROLLER_UNKNOWN;
        s_claimed = false;
        break;
    }

    bool changed = (was != s_controller) || (was_claimed != s_claimed);
    xSemaphoreGive(s_peers_lock);

    if (!changed) {
        return;
    }

    /* The announcement carries the claim, so it has to be rebuilt when the
     * claim moves — otherwise a node that stood down keeps telling the
     * network it is in charge. */
    int length = build_announce();
    if (length > 0 && length < (int)sizeof(s_announce)) {
        s_announce_len = length;
    }

    switch (s_controller) {
    case OAL_CONTROLLER_SELF:
        ESP_LOGI(TAG, "controller: this node%s", s_claimed ? " (claimed)" : "");
        break;
    case OAL_CONTROLLER_PEER:
        ESP_LOGI(TAG, "controller: %s at %s", s_controller_peer.name, s_controller_peer.address);
        break;
    default:
        ESP_LOGI(TAG, "controller: none");
        break;
    }
}

size_t oal_discovery_peer_count(void)
{
    if (s_peers_lock == NULL
        || xSemaphoreTake(s_peers_lock, pdMS_TO_TICKS(100)) != pdTRUE) {
        return 0;
    }
    size_t live = oal_peer_table_count_live(&s_peers, esp_timer_get_time());
    xSemaphoreGive(s_peers_lock);
    return live;
}

/*
 * One parse for both jobs. The message was parsed once to test for a probe
 * and thrown away; parsing it twice to also read an announce would double
 * the cost of every datagram on a shared multicast group.
 *
 * Returns true if a probe was seen and the caller should answer it.
 */
static bool handle_message(const char *buf, int len, const struct sockaddr_in *from)
{
    cJSON *root = cJSON_ParseWithLength(buf, len);
    if (root == NULL) {
        return false;
    }

    const cJSON *type = cJSON_GetObjectItemCaseSensitive(root, "type");
    const cJSON *oal = cJSON_GetObjectItemCaseSensitive(root, "oal");
    bool compatible = cJSON_IsString(oal) && strncmp(oal->valuestring, "0.", 2) == 0;
    bool probe = false;

    if (compatible && cJSON_IsString(type)) {
        if (strcmp(type->valuestring, "probe") == 0) {
            probe = true;
        } else if (strcmp(type->valuestring, "announce") == 0) {
            remember_peer(root, from);
        }
    }

    cJSON_Delete(root);
    return probe;
}

static void discovery_task(void *arg)
{
    int sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_IP);
    if (sock < 0) {
        ESP_LOGE(TAG, "socket() failed: errno %d", errno);
        vTaskDelete(NULL);
        return;
    }

    int reuse = 1;
    setsockopt(sock, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof(reuse));

    struct sockaddr_in bind_addr = {
        .sin_family = AF_INET,
        .sin_port = htons(OAL_DISCOVERY_PORT),
        .sin_addr.s_addr = htonl(INADDR_ANY),
    };
    if (bind(sock, (struct sockaddr *)&bind_addr, sizeof(bind_addr)) < 0) {
        ESP_LOGE(TAG, "bind() failed: errno %d", errno);
        close(sock);
        vTaskDelete(NULL);
        return;
    }

    struct ip_mreq mreq = {
        .imr_multiaddr.s_addr = inet_addr(OAL_DISCOVERY_GROUP),
        .imr_interface.s_addr = htonl(INADDR_ANY),
    };
    if (setsockopt(sock, IPPROTO_IP, IP_ADD_MEMBERSHIP, &mreq, sizeof(mreq)) < 0) {
        ESP_LOGW(TAG, "joining multicast group failed: errno %d", errno);
    }

    uint8_t ttl = 1; /* local-first: link-local multicast only */
    setsockopt(sock, IPPROTO_IP, IP_MULTICAST_TTL, &ttl, sizeof(ttl));

    struct sockaddr_in group_addr = {
        .sin_family = AF_INET,
        .sin_port = htons(OAL_DISCOVERY_PORT),
        .sin_addr.s_addr = inet_addr(OAL_DISCOVERY_GROUP),
    };

    ESP_LOGI(TAG, "announcing as %s (%s) every %d ms", s_config.id, s_config.name,
             OAL_ANNOUNCE_INTERVAL_MS);

    char rx[512];
    TickType_t next_announce = xTaskGetTickCount();

    /* Three announce intervals before the first election. Claiming the
     * moment the network comes up would have every node decide alone
     * before it has heard anybody, and a Hub that was already running
     * would be discovered a moment too late. */
    const TickType_t settle_until =
        xTaskGetTickCount() + pdMS_TO_TICKS(OAL_ANNOUNCE_INTERVAL_MS * 3);

    for (;;) {
        TickType_t now = xTaskGetTickCount();
        if ((int32_t)(now - next_announce) >= 0) {
            if ((int32_t)(now - settle_until) >= 0) {
                run_election();
            }
            if (sendto(sock, s_announce, s_announce_len, 0,
                       (struct sockaddr *)&group_addr, sizeof(group_addr)) < 0) {
                ESP_LOGW(TAG, "announce failed: errno %d", errno);
            }
            next_announce = now + pdMS_TO_TICKS(OAL_ANNOUNCE_INTERVAL_MS);
        }

        int32_t remaining_ticks = (int32_t)(next_announce - xTaskGetTickCount());
        if (remaining_ticks < 1) {
            remaining_ticks = 1;
        }
        uint32_t remaining_ms = pdTICKS_TO_MS((uint32_t)remaining_ticks);
        struct timeval timeout = {
            .tv_sec = remaining_ms / 1000,
            .tv_usec = (remaining_ms % 1000) * 1000,
        };
        setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));

        struct sockaddr_in from;
        socklen_t from_len = sizeof(from);
        int len = recvfrom(sock, rx, sizeof(rx) - 1, 0, (struct sockaddr *)&from, &from_len);
        if (len > 0 && handle_message(rx, len, &from)) {
            /* Random 0-500 ms delay avoids a reply burst from many nodes. */
            vTaskDelay(pdMS_TO_TICKS(esp_random() % 500));
            sendto(sock, s_announce, s_announce_len, 0, (struct sockaddr *)&from, from_len);
        }
    }
}

esp_err_t oal_discovery_start(const oal_discovery_config_t *config)
{
    if (config == NULL || config->roles == OAL_ROLE_NONE || config->hardware_profile == NULL
        || config->firmware_version == NULL) {
        return ESP_ERR_INVALID_ARG;
    }

    s_config = *config;
    s_announce_len = build_announce();
    if (s_announce_len <= 0 || s_announce_len >= (int)sizeof(s_announce)) {
        return ESP_ERR_INVALID_SIZE;
    }

    /* Before the task starts, so a peer cannot arrive while the lock that
     * guards the table is still null. */
    if (s_peers_lock == NULL) {
        s_peers_lock = xSemaphoreCreateMutex();
        if (s_peers_lock == NULL) {
            return ESP_ERR_NO_MEM;
        }
    }

    if (xTaskCreate(discovery_task, "oal_discovery", 4096, NULL, 5, NULL) != pdPASS) {
        return ESP_ERR_NO_MEM;
    }
    return ESP_OK;
}
