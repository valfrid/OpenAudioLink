#pragma once

#include <stddef.h>
#include <stdint.h>

#include "esp_err.h"

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

#ifdef __cplusplus
}
#endif
