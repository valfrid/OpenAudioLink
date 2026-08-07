#include "oal_config.h"

#include <stdbool.h>
#include <string.h>

#include "esp_log.h"
#include "nvs.h"
#include "nvs_flash.h"
#include "oal_pcm.h"

static const char *TAG = "oal_config";

/* Shares the namespace oal_wifi uses: one place holds everything a node
 * keeps across reboots, so erasing NVS resets the node completely rather
 * than leaving half a configuration behind. */
#define NVS_NAMESPACE "oal"
#define NVS_KEY_ROLES "roles"
#define NVS_KEY_NAME  "name"
#define NVS_KEY_CHANNEL "channel"
#define NVS_KEY_VOLUME "volume"

/* Listed in ARCHITECTURE.md section 2 order, so formatted output always
 * reads the same way regardless of the order roles were set in — and the
 * same way the Hub renders it. */
static const struct {
    oal_role_t role;
    const char *name;
} ROLES[] = {
    { OAL_ROLE_CONTROLLER, "controller" },
    { OAL_ROLE_PRODUCER, "producer" },
    { OAL_ROLE_CONSUMER, "consumer" },
};

#define ROLE_COUNT (sizeof(ROLES) / sizeof(ROLES[0]))

const char *oal_role_name(oal_role_t role)
{
    for (size_t i = 0; i < ROLE_COUNT; i++) {
        if (ROLES[i].role == role) {
            return ROLES[i].name;
        }
    }
    return NULL;
}

oal_role_t oal_role_from_name(const char *name)
{
    if (name == NULL) {
        return OAL_ROLE_NONE;
    }
    for (size_t i = 0; i < ROLE_COUNT; i++) {
        if (strcmp(ROLES[i].name, name) == 0) {
            return ROLES[i].role;
        }
    }
    return OAL_ROLE_NONE;
}

oal_roles_t oal_roles_parse(const char *list)
{
    if (list == NULL) {
        return OAL_ROLE_NONE;
    }

    oal_roles_t roles = OAL_ROLE_NONE;
    const char *start = list;
    for (;;) {
        /* Trim leading spaces so "producer, consumer" works the way anyone
         * typing it into a form would expect. */
        while (*start == ' ') {
            start++;
        }

        const char *comma = strchr(start, ',');
        size_t len = (comma != NULL) ? (size_t)(comma - start) : strlen(start);
        while (len > 0 && start[len - 1] == ' ') {
            len--;
        }

        if (len > 0) {
            char name[16];
            if (len >= sizeof(name)) {
                return OAL_ROLE_NONE;
            }
            memcpy(name, start, len);
            name[len] = '\0';

            oal_role_t role = oal_role_from_name(name);
            if (role == OAL_ROLE_NONE) {
                return OAL_ROLE_NONE; /* one bad name invalidates the list */
            }
            roles |= role;
        }

        if (comma == NULL) {
            break;
        }
        start = comma + 1;
    }

    return roles;
}

/* Both formatters walk ROLES in order and differ only in punctuation, so
 * they share one writer rather than drifting apart. */
static int format_roles(oal_roles_t roles, char *out, size_t out_size,
                        const char *open, const char *quote, const char *separator,
                        const char *close)
{
    if (out == NULL || out_size == 0) {
        return -1;
    }

    size_t used = 0;
    bool first = true;

#define APPEND(text)                              \
    do {                                          \
        size_t n = strlen(text);                  \
        if (used + n >= out_size) return -1;      \
        memcpy(out + used, (text), n);            \
        used += n;                                \
    } while (0)

    APPEND(open);
    for (size_t i = 0; i < ROLE_COUNT; i++) {
        if ((roles & ROLES[i].role) == 0) {
            continue;
        }
        if (!first) {
            APPEND(separator);
        }
        first = false;
        APPEND(quote);
        APPEND(ROLES[i].name);
        APPEND(quote);
    }
    APPEND(close);
#undef APPEND

    out[used] = '\0';
    return (int)used;
}

int oal_roles_to_list(oal_roles_t roles, char *out, size_t out_size)
{
    return format_roles(roles, out, out_size, "", "", ",", "");
}

int oal_roles_to_json(oal_roles_t roles, char *out, size_t out_size)
{
    return format_roles(roles, out, out_size, "[", "\"", ",", "]");
}

oal_roles_t oal_config_get_roles(void)
{
    nvs_handle_t nvs;
    if (nvs_open(NVS_NAMESPACE, NVS_READONLY, &nvs) != ESP_OK) {
        return OAL_ROLES_DEFAULT;
    }

    uint32_t stored = 0;
    esp_err_t err = nvs_get_u32(nvs, NVS_KEY_ROLES, &stored);
    nvs_close(nvs);

    /* A stored value from a newer firmware could carry bits this build
     * does not know. Keep the ones it understands rather than refusing to
     * start in a role the operator asked for. */
    oal_roles_t roles = (oal_roles_t)stored & OAL_ROLES_ALL;
    if (err != ESP_OK || roles == OAL_ROLE_NONE) {
        return OAL_ROLES_DEFAULT;
    }
    return roles;
}

esp_err_t oal_config_set_roles(oal_roles_t roles)
{
    if (roles == OAL_ROLE_NONE || (roles & ~(oal_roles_t)OAL_ROLES_ALL) != 0) {
        return ESP_ERR_INVALID_ARG;
    }

    nvs_handle_t nvs;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READWRITE, &nvs);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "nvs_open failed: %s", esp_err_to_name(err));
        return err;
    }

    err = nvs_set_u32(nvs, NVS_KEY_ROLES, (uint32_t)roles);
    if (err == ESP_OK) {
        err = nvs_commit(nvs);
    }
    nvs_close(nvs);

    if (err == ESP_OK) {
        char text[OAL_ROLES_STR_MAX];
        if (oal_roles_to_list(roles, text, sizeof(text)) > 0) {
            ESP_LOGI(TAG, "roles set to %s (takes effect on reboot)", text);
        }
    }
    return err;
}

/*
 * The channel profile (decision 10). Stored as the wire name rather than
 * the enum value, so a stored setting survives the enum being reordered
 * and can be read out of an NVS dump without a lookup table.
 */
oal_channel_t oal_config_get_channel(void)
{
    nvs_handle_t nvs;
    if (nvs_open(NVS_NAMESPACE, NVS_READONLY, &nvs) != ESP_OK) {
        return OAL_CHANNEL_DEFAULT;
    }

    char stored[16];
    size_t length = sizeof(stored);
    esp_err_t err = nvs_get_str(nvs, NVS_KEY_CHANNEL, stored, &length);
    nvs_close(nvs);

    oal_channel_t channel;
    if (err != ESP_OK || !oal_channel_parse(stored, &channel)) {
        return OAL_CHANNEL_DEFAULT;
    }
    return channel;
}

esp_err_t oal_config_set_channel(oal_channel_t channel)
{
    /* Round-tripping the name is the validity check: an out-of-range enum
     * formats as "stereo" and would be stored as a setting nobody asked
     * for. */
    oal_channel_t parsed;
    const char *name = oal_channel_name(channel);
    if (!oal_channel_parse(name, &parsed) || parsed != channel) {
        return ESP_ERR_INVALID_ARG;
    }

    nvs_handle_t nvs;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READWRITE, &nvs);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "nvs_open failed: %s", esp_err_to_name(err));
        return err;
    }

    err = nvs_set_str(nvs, NVS_KEY_CHANNEL, name);
    if (err == ESP_OK) {
        err = nvs_commit(nvs);
    }
    nvs_close(nvs);

    if (err == ESP_OK) {
        ESP_LOGI(TAG, "channel set to %s (takes effect on reboot)", name);
    }
    return err;
}

/*
 * Playback volume. Stored as the percentage rather than as the computed
 * gain, so the curve can change without every node in the house getting
 * quieter or louder on the next update.
 */
uint8_t oal_config_get_volume(void)
{
    nvs_handle_t nvs;
    if (nvs_open(NVS_NAMESPACE, NVS_READONLY, &nvs) != ESP_OK) {
        return OAL_VOLUME_DEFAULT;
    }

    uint8_t stored = OAL_VOLUME_DEFAULT;
    esp_err_t err = nvs_get_u8(nvs, NVS_KEY_VOLUME, &stored);
    nvs_close(nvs);

    if (err != ESP_OK || stored > 100) {
        return OAL_VOLUME_DEFAULT;
    }
    return stored;
}

esp_err_t oal_config_set_volume(uint8_t percent)
{
    if (percent > 100) {
        return ESP_ERR_INVALID_ARG;
    }

    nvs_handle_t nvs;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READWRITE, &nvs);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "nvs_open failed: %s", esp_err_to_name(err));
        return err;
    }

    err = nvs_set_u8(nvs, NVS_KEY_VOLUME, percent);
    if (err == ESP_OK) {
        err = nvs_commit(nvs);
    }
    nvs_close(nvs);
    return err;
}

esp_err_t oal_config_get_name(char *out, size_t out_size)
{
    if (out == NULL || out_size == 0) {
        return ESP_ERR_INVALID_ARG;
    }
    out[0] = '\0';

    nvs_handle_t nvs;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READONLY, &nvs);
    if (err != ESP_OK) {
        return ESP_ERR_NVS_NOT_FOUND;
    }

    size_t len = out_size;
    err = nvs_get_str(nvs, NVS_KEY_NAME, out, &len);
    nvs_close(nvs);

    if (err != ESP_OK || out[0] == '\0') {
        out[0] = '\0';
        return ESP_ERR_NVS_NOT_FOUND;
    }
    return ESP_OK;
}

esp_err_t oal_config_set_name(const char *name)
{
    nvs_handle_t nvs;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READWRITE, &nvs);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "nvs_open failed: %s", esp_err_to_name(err));
        return err;
    }

    /* An empty name erases the key rather than storing "", so the node
     * falls back to its MAC-derived default instead of announcing a blank
     * name that no list can show. */
    if (name == NULL || name[0] == '\0') {
        err = nvs_erase_key(nvs, NVS_KEY_NAME);
        if (err == ESP_ERR_NVS_NOT_FOUND) {
            err = ESP_OK; /* already absent, which is the requested state */
        }
    } else if (strlen(name) >= OAL_NAME_MAX) {
        nvs_close(nvs);
        return ESP_ERR_INVALID_SIZE;
    } else {
        err = nvs_set_str(nvs, NVS_KEY_NAME, name);
    }

    if (err == ESP_OK) {
        err = nvs_commit(nvs);
    }
    nvs_close(nvs);
    return err;
}
