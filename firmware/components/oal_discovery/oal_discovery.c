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

static int build_announce(void)
{
    char roles[OAL_ROLES_STR_MAX];
    if (oal_roles_to_json(s_config.roles, roles, sizeof(roles)) < 0) {
        return -1;
    }

    return snprintf(s_announce, sizeof(s_announce),
                    "{\"oal\":\"" OAL_PROTOCOL_VERSION "\",\"type\":\"announce\","
                    "\"id\":\"%s\",\"name\":\"%s\",\"roles\":%s,"
                    "\"hw\":\"%s\",\"fw\":\"%s\",\"ctrlPort\":%d}",
                    s_config.id, s_config.name, roles,
                    s_config.hardware_profile, s_config.firmware_version,
                    OAL_DEVICE_CONTROL_PORT);
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

    for (;;) {
        TickType_t now = xTaskGetTickCount();
        if ((int32_t)(now - next_announce) >= 0) {
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
