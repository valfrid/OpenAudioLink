#include "oal_discovery.h"

#include <string.h>

#include "cJSON.h"
#include "esp_log.h"
#include "esp_random.h"
#include "freertos/FreeRTOS.h"
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

/* A probe is any message with type "probe" and a compatible (0.x) suite version. */
static bool is_probe(const char *buf, int len)
{
    cJSON *root = cJSON_ParseWithLength(buf, len);
    if (root == NULL) {
        return false;
    }

    const cJSON *type = cJSON_GetObjectItemCaseSensitive(root, "type");
    const cJSON *oal = cJSON_GetObjectItemCaseSensitive(root, "oal");
    bool probe = cJSON_IsString(type) && strcmp(type->valuestring, "probe") == 0
                 && cJSON_IsString(oal) && strncmp(oal->valuestring, "0.", 2) == 0;
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
        if (len > 0 && is_probe(rx, len)) {
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

    if (xTaskCreate(discovery_task, "oal_discovery", 4096, NULL, 5, NULL) != pdPASS) {
        return ESP_ERR_NO_MEM;
    }
    return ESP_OK;
}
