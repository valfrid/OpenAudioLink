#pragma once

#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Protocol-suite constants; see protocol/README.md and protocol/DISCOVERY.md. */
#define OAL_PROTOCOL_VERSION      "0.1"
#define OAL_DISCOVERY_GROUP       "239.255.41.10"
#define OAL_DISCOVERY_PORT        41000
#define OAL_DEVICE_CONTROL_PORT   41001
#define OAL_ANNOUNCE_INTERVAL_MS  5000

typedef struct {
    char id[40];                    /* "mac-…" or "oal-…", see protocol/IDENTITY.md */
    char name[32];
    const char *role;               /* "receiver", "analog-source" */
    const char *hardware_profile;   /* e.g. "esp32c3-devkit" */
    const char *firmware_version;   /* semver */
} oal_discovery_config_t;

/**
 * Starts the discovery task. Requires the network to be up (got IP).
 * Announces every OAL_ANNOUNCE_INTERVAL_MS to the multicast group and
 * answers probes with a unicast announce.
 */
esp_err_t oal_discovery_start(const oal_discovery_config_t *config);

#ifdef __cplusplus
}
#endif
