#pragma once

#include <stddef.h>
#include <stdint.h>

#include "esp_err.h"
#include "oal_channel.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Node configuration held in NVS rather than compiled in (decision 5).
 *
 * Roles are logical, not device-bound (ARCHITECTURE.md section 2), so a
 * node carries a *set* of them: an analog source that also plays is both
 * a producer and a consumer, and that case is the reason this is a mask
 * and not a single value. One firmware image serves every node; what a
 * given board does is configuration, not a separate binary.
 *
 * Controller is defined here because the Hub announces it and a node may
 * take limited controller duties in standalone mode. No node claims it
 * today.
 */
typedef enum {
    OAL_ROLE_NONE       = 0,
    OAL_ROLE_CONSUMER   = 1u << 0, /* receives RTP, jitter buffer, I2S out */
    OAL_ROLE_PRODUCER   = 1u << 1, /* I2S in, packetises, sends RTP */
    OAL_ROLE_CONTROLLER = 1u << 2, /* discovery, selection, route ownership */
} oal_role_t;

typedef uint32_t oal_roles_t;

/** Every role a node may hold, for validating input. */
#define OAL_ROLES_ALL (OAL_ROLE_CONSUMER | OAL_ROLE_PRODUCER | OAL_ROLE_CONTROLLER)

/** Unconfigured nodes are consumers; that is the common case by far. */
#define OAL_ROLES_DEFAULT (OAL_ROLE_CONSUMER)

/** Wire name of a single role, or NULL if the value is not one role. */
const char *oal_role_name(oal_role_t role);

/** Single role by wire name, or OAL_ROLE_NONE if unrecognised. */
oal_role_t oal_role_from_name(const char *name);

/**
 * Parses a comma-separated list such as "producer,consumer".
 * Returns OAL_ROLE_NONE if any element is unrecognised, so a typo is
 * rejected outright rather than silently dropping a role.
 */
oal_roles_t oal_roles_parse(const char *list);

/** Writes `consumer,producer`. Returns length, or -1 if it will not fit. */
int oal_roles_to_list(oal_roles_t roles, char *out, size_t out_size);

/** Writes `["consumer","producer"]`. Returns length, or -1 if it will not fit. */
int oal_roles_to_json(oal_roles_t roles, char *out, size_t out_size);

/** Longest output either formatter can produce, including the terminator. */
#define OAL_ROLES_STR_MAX 64

/** Stored roles, or OAL_ROLES_DEFAULT when unset or unreadable. */
oal_roles_t oal_config_get_roles(void);

/** Persists roles. Rejects OAL_ROLE_NONE and unknown bits. */
esp_err_t oal_config_set_roles(oal_roles_t roles);

/*
 * Which of the stream's two channels this node plays (decision 10). A
 * single mono or one-side speaker is a first-class arrangement here, not
 * a degraded stereo one, so this sits beside the roles rather than being
 * inferred from anything.
 */

/** Stored channel profile, or stereo when unset or unreadable. */
oal_channel_t oal_config_get_channel(void);

/** Persists the channel profile. Rejects values outside the enum. */
esp_err_t oal_config_set_channel(oal_channel_t channel);

/** Longest node name, including the terminator. Matches the announce field. */
#define OAL_NAME_MAX 32

/**
 * Copies the stored node name into @p out.
 * @return ESP_OK, or ESP_ERR_NVS_NOT_FOUND when no name has been set —
 *         in which case the caller supplies its own default.
 */
esp_err_t oal_config_get_name(char *out, size_t out_size);

/** Persists the node name. An empty name clears it, restoring the default. */
esp_err_t oal_config_set_name(const char *name);

#ifdef __cplusplus
}
#endif
