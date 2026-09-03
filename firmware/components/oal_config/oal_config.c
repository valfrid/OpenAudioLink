#include "oal_config.h"

#include <stdbool.h>
#include <string.h>

#include "esp_log.h"
#include "nvs.h"
#include "nvs_flash.h"
#include "oal_eq.h"
#include "oal_playout.h"
#include "oal_pcm.h"

static const char *TAG = "oal_config";

/* Shares the namespace oal_wifi uses: one place holds everything a node
 * keeps across reboots, so erasing NVS resets the node completely rather
 * than leaving half a configuration behind. */
#define NVS_NAMESPACE "oal"
#define NVS_KEY_ROLES "roles"
#define NVS_KEY_NAME  "name"
#define NVS_KEY_CHANNEL "channel"
#define NVS_KEY_DELAY_MS "delay_ms"
#define NVS_KEY_RING_MS  "ring_ms"
#define NVS_KEY_VOLUME "volume"
#define NVS_KEY_OUTPUT "output"
#define NVS_KEY_INPUT  "input"
#define NVS_KEY_MIC_GAIN "mic_gain"
#define NVS_KEY_EQ_LEFT "eq_l"
#define NVS_KEY_EQ_RIGHT "eq_r"
#define NVS_KEY_EQ_ON "eq_on"

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
/*
 * Extra playout delay for this node, in milliseconds.
 *
 * Per node and not per installation, because it corrects a difference
 * *between* nodes: a USB dongle plays tens of milliseconds later than an
 * I²S DAC given the same packet, so the DAC has to be held back to meet
 * it. Which node needs the trim depends on what is plugged into it, which
 * is exactly the kind of fact decision 5 keeps in NVS rather than in a
 * build.
 *
 * Only ever positive. Nothing can play a sample before it arrives.
 */
uint32_t oal_config_get_delay_ms(void)
{
    nvs_handle_t nvs;
    if (nvs_open(NVS_NAMESPACE, NVS_READONLY, &nvs) != ESP_OK) {
        return 0;
    }
    uint32_t stored = 0;
    esp_err_t err = nvs_get_u32(nvs, NVS_KEY_DELAY_MS, &stored);
    nvs_close(nvs);
    if (err != ESP_OK || stored > OAL_DELAY_MS_MAX) {
        return 0;
    }
    return stored;
}

esp_err_t oal_config_set_delay_ms(uint32_t delay_ms)
{
    if (delay_ms > OAL_DELAY_MS_MAX) {
        return ESP_ERR_INVALID_ARG;
    }

    nvs_handle_t nvs;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READWRITE, &nvs);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "nvs_open failed: %s", esp_err_to_name(err));
        return err;
    }
    err = nvs_set_u32(nvs, NVS_KEY_DELAY_MS, delay_ms);
    if (err == ESP_OK) {
        err = nvs_commit(nvs);
    }
    nvs_close(nvs);
    return err;
}

/*
 * The ring, which is read once at boot and never again.
 *
 * Out-of-range falls back to the default rather than being honoured or
 * refused. NVS outlives firmware: a node configured for 1000 ms and then
 * rolled back to a build that only understands 200 must come up playing,
 * not refuse to start its output stage over a number it cannot use.
 */
uint32_t oal_config_get_ring_ms(void)
{
    nvs_handle_t nvs;
    if (nvs_open(NVS_NAMESPACE, NVS_READONLY, &nvs) != ESP_OK) {
        return OAL_RING_MS_DEFAULT;
    }
    uint32_t stored = 0;
    esp_err_t err = nvs_get_u32(nvs, NVS_KEY_RING_MS, &stored);
    nvs_close(nvs);
    if (err != ESP_OK || stored < OAL_RING_MS_MIN || stored > OAL_RING_MS_MAX) {
        return OAL_RING_MS_DEFAULT;
    }
    return stored;
}

esp_err_t oal_config_set_ring_ms(uint32_t ring_ms)
{
    if (ring_ms < OAL_RING_MS_MIN || ring_ms > OAL_RING_MS_MAX) {
        return ESP_ERR_INVALID_ARG;
    }

    nvs_handle_t nvs;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READWRITE, &nvs);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "nvs_open failed: %s", esp_err_to_name(err));
        return err;
    }
    err = nvs_set_u32(nvs, NVS_KEY_RING_MS, ring_ms);
    if (err == ESP_OK) {
        err = nvs_commit(nvs);
    }
    nvs_close(nvs);
    return err;
}

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
 * The output stage (docs/USB-AUDIO.md). Stored as the wire name for the
 * same reasons as the channel profile above, and read with the same
 * fallback — an absent key means every node configured before this setting
 * existed keeps playing through the DAC it is wired to.
 */
oal_output_t oal_config_get_output(void)
{
    nvs_handle_t nvs;
    if (nvs_open(NVS_NAMESPACE, NVS_READONLY, &nvs) != ESP_OK) {
        return OAL_OUTPUT_DEFAULT;
    }

    char stored[16];
    size_t length = sizeof(stored);
    esp_err_t err = nvs_get_str(nvs, NVS_KEY_OUTPUT, stored, &length);
    nvs_close(nvs);

    oal_output_t output;
    if (err != ESP_OK || !oal_output_parse(stored, &output)) {
        return OAL_OUTPUT_DEFAULT;
    }
    return output;
}

/*
 * What this Producer captures from, the mirror of the output stage above
 * and stored the same way.
 *
 * Read once at boot, because it decides a set of pins and which end of the
 * I2S bus generates the clocks -- neither of which can change under a
 * running capture. See oal_input.h for why the two inputs must not share
 * pins.
 */
oal_input_t oal_config_get_input(void)
{
    nvs_handle_t nvs;
    if (nvs_open(NVS_NAMESPACE, NVS_READONLY, &nvs) != ESP_OK) {
        return OAL_INPUT_DEFAULT;
    }

    char stored[16];
    size_t length = sizeof(stored);
    esp_err_t err = nvs_get_str(nvs, NVS_KEY_INPUT, stored, &length);
    nvs_close(nvs);

    oal_input_t input;
    if (err != ESP_OK || !oal_input_parse(stored, &input)) {
        return OAL_INPUT_DEFAULT;
    }
    return input;
}

esp_err_t oal_config_set_input(oal_input_t input)
{
    /* Round-tripping the name is the validity check, as for the output
     * stage and the channel: an out-of-range enum formats as "line" and
     * would be stored as a setting nobody asked for. */
    oal_input_t parsed;
    const char *name = oal_input_name(input);
    if (!oal_input_parse(name, &parsed) || parsed != input) {
        return ESP_ERR_INVALID_ARG;
    }

    nvs_handle_t nvs;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READWRITE, &nvs);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "nvs_open failed: %s", esp_err_to_name(err));
        return err;
    }

    err = nvs_set_str(nvs, NVS_KEY_INPUT, name);
    if (err == ESP_OK) {
        err = nvs_commit(nvs);
    }
    nvs_close(nvs);
    return err;
}

esp_err_t oal_config_set_output(oal_output_t output)
{
    /* Round-tripping the name is the validity check, as for the channel:
     * an out-of-range enum formats as "i2s" and would be stored as a
     * setting nobody asked for. */
    oal_output_t parsed;
    const char *name = oal_output_name(output);
    if (!oal_output_parse(name, &parsed) || parsed != output) {
        return ESP_ERR_INVALID_ARG;
    }

    nvs_handle_t nvs;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READWRITE, &nvs);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "nvs_open failed: %s", esp_err_to_name(err));
        return err;
    }

    err = nvs_set_str(nvs, NVS_KEY_OUTPUT, name);
    if (err == ESP_OK) {
        err = nvs_commit(nvs);
    }
    nvs_close(nvs);

    if (err == ESP_OK) {
        ESP_LOGI(TAG, "output stage set to %s (takes effect on reboot)", name);
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

/*
 * Capture gain for a microphone, in whole decibels.
 *
 * Stored beside the volume and shaped like it, and the pair is worth
 * reading together: volume attenuates a stream that arrives near full
 * scale, this amplifies one that arrives 40 dB below it. Opposite ends of
 * the same chain, and neither can do the other's job -- which is why the
 * first microphone stream was audible but far too quiet even with every
 * consumer at 100%.
 *
 * Zero for a line input, and zero is the default, so nothing that is not a
 * microphone is affected by the existence of this setting.
 */
uint8_t oal_config_get_mic_gain_db(void)
{
    nvs_handle_t nvs;
    if (nvs_open(NVS_NAMESPACE, NVS_READONLY, &nvs) != ESP_OK) {
        return 0;
    }
    uint8_t stored = 0;
    esp_err_t err = nvs_get_u8(nvs, NVS_KEY_MIC_GAIN, &stored);
    nvs_close(nvs);
    if (err != ESP_OK || stored > OAL_BOOST_DB_MAX) {
        return 0;
    }
    return stored;
}

esp_err_t oal_config_set_mic_gain_db(uint8_t db)
{
    if (db > OAL_BOOST_DB_MAX) {
        return ESP_ERR_INVALID_ARG;
    }
    nvs_handle_t nvs;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READWRITE, &nvs);
    if (err != ESP_OK) {
        return err;
    }
    err = nvs_set_u8(nvs, NVS_KEY_MIC_GAIN, db);
    if (err == ESP_OK) {
        err = nvs_commit(nvs);
    }
    nvs_close(nvs);
    return err;
}

/*
 * Room correction (docs/ROOM-CALIBRATION.md).
 *
 * Four settings, and the shape of them is the feature:
 *
 *   eq_l, eq_r   one vector per OUTPUT channel, as readable text. A
 *                correction belongs to a loudspeaker, and a stereo node
 *                drives two of them standing in two different corners.
 *   eq_on        whether to run it, keeping the coefficients either way.
 *                Without this, comparing corrected against uncorrected
 *                means deleting a profile and measuring again to get it
 *                back -- so nobody would ever check, which is the one
 *                thing worth checking.
 *
 * There is deliberately no headroom setting. It is a function of the
 * filters, so the playout computes it from them -- a stored figure could
 * only be a second opinion about the same arithmetic, and it would be the
 * wrong one the moment somebody edited a vector by hand.
 */
static esp_err_t get_eq_text(const char *key, char *out, size_t size)
{
    if (out == NULL || size == 0) {
        return ESP_ERR_INVALID_ARG;
    }
    out[0] = '\0';

    nvs_handle_t nvs;
    if (nvs_open(NVS_NAMESPACE, NVS_READONLY, &nvs) != ESP_OK) {
        return ESP_OK;   /* nothing stored is an empty vector, not a fault */
    }
    size_t length = size;
    esp_err_t err = nvs_get_str(nvs, key, out, &length);
    nvs_close(nvs);

    if (err != ESP_OK) {
        out[0] = '\0';
    }
    return ESP_OK;
}

static esp_err_t set_eq_text(const char *key, const char *text)
{
    /*
     * Parsed before it is stored, so a vector that cannot be read back is
     * refused at the point somebody can still be told about it rather than
     * discovered at the next boot as a speaker that lost its correction.
     */
    oal_eq_curve_t curve;
    if (!oal_eq_parse(text == NULL ? "" : text, &curve)) {
        return ESP_ERR_INVALID_ARG;
    }

    char normalised[OAL_EQ_TEXT_MAX];
    if (oal_eq_format(&curve, normalised, sizeof(normalised)) < 0) {
        return ESP_ERR_INVALID_ARG;
    }

    nvs_handle_t nvs;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READWRITE, &nvs);
    if (err != ESP_OK) {
        return err;
    }
    err = nvs_set_str(nvs, key, normalised);
    if (err == ESP_OK) {
        err = nvs_commit(nvs);
    }
    nvs_close(nvs);
    return err;
}

esp_err_t oal_config_get_eq(oal_channel_side_t side, char *out, size_t size)
{
    return get_eq_text(side == OAL_SIDE_RIGHT ? NVS_KEY_EQ_RIGHT : NVS_KEY_EQ_LEFT, out, size);
}

esp_err_t oal_config_set_eq(oal_channel_side_t side, const char *text)
{
    return set_eq_text(side == OAL_SIDE_RIGHT ? NVS_KEY_EQ_RIGHT : NVS_KEY_EQ_LEFT, text);
}

bool oal_config_get_eq_enabled(void)
{
    nvs_handle_t nvs;
    if (nvs_open(NVS_NAMESPACE, NVS_READONLY, &nvs) != ESP_OK) {
        return false;
    }
    uint8_t stored = 0;
    esp_err_t err = nvs_get_u8(nvs, NVS_KEY_EQ_ON, &stored);
    nvs_close(nvs);

    /* Off unless it was turned on. A node updated into a firmware that has
     * this must sound exactly as it did before. */
    return err == ESP_OK && stored != 0;
}

esp_err_t oal_config_set_eq_enabled(bool on)
{
    nvs_handle_t nvs;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READWRITE, &nvs);
    if (err != ESP_OK) {
        return err;
    }
    err = nvs_set_u8(nvs, NVS_KEY_EQ_ON, on ? 1 : 0);
    if (err == ESP_OK) {
        err = nvs_commit(nvs);
    }
    nvs_close(nvs);
    return err;
}

/**
 * Reads the stored correction and hands it to the playout.
 *
 * Here rather than in either of them: the playout takes values and knows
 * nothing about storage, and the audio component cannot depend on this one
 * because this one already depends on it. So the join lives on the side
 * that can see both.
 */
void oal_config_apply_eq(void)
{
    char text[OAL_EQ_TEXT_MAX];
    oal_eq_curve_t left = { 0 };
    oal_eq_curve_t right = { 0 };

    if (oal_config_get_eq(OAL_SIDE_LEFT, text, sizeof(text)) == ESP_OK) {
        (void)oal_eq_parse(text, &left);
    }
    if (oal_config_get_eq(OAL_SIDE_RIGHT, text, sizeof(text)) == ESP_OK) {
        (void)oal_eq_parse(text, &right);
    }

    oal_playout_set_eq(&left, &right, oal_config_get_eq_enabled());
}
