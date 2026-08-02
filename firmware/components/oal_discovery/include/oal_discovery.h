#pragma once

#include "esp_err.h"
#include "oal_config.h"
#include "oal_election.h"
#include "oal_peers.h"

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
    oal_roles_t roles;              /* set, not one value; see oal_config.h */
    const char *hardware_profile;   /* e.g. "esp32s3-devkit" */
    const char *firmware_version;   /* semver */
} oal_discovery_config_t;

/**
 * Starts the discovery task. Requires the network to be up (got IP).
 * Announces every OAL_ANNOUNCE_INTERVAL_MS to the multicast group and
 * answers probes with a unicast announce.
 */
esp_err_t oal_discovery_start(const oal_discovery_config_t *config);

/*
 * The peer table (decision 9).
 *
 * Announcements from other nodes were already arriving on this socket and
 * being discarded, so every node knew the network existed and nothing
 * about who was on it. That is workable only while a PC is the Controller.
 * A party system has no PC: the turntable holds the Controller role, and a
 * Controller that cannot see the speakers cannot route anything to them.
 *
 * Peers are recorded here and nothing acts on them yet. Claiming the role
 * and answering joins come next; this is the table they will read.
 */

/**
 * Copies the live peers into @p out, newest announcement first, and
 * returns how many were written. See oal_peers.h for the table itself.
 */
size_t oal_discovery_peers(oal_peer_t *out, size_t max);

/** How many peers are currently live. Cheaper than copying the table. */
size_t oal_discovery_peer_count(void);

/*
 * Who holds the Controller role (decision 9). Recomputed every announce
 * interval from the peer table; see oal_election.h for the precedence.
 *
 * A node holding Producer that finds nobody claims the role and says so in
 * its announcements, which is how a turntable at a party ends up running
 * the party without anybody configuring it.
 */
typedef enum {
    OAL_CONTROLLER_UNKNOWN = 0, /* nobody holds it, or we are still settling */
    OAL_CONTROLLER_SELF,
    OAL_CONTROLLER_PEER,
} oal_controller_who_t;

/**
 * Reports who holds Controller, copying the holder into @p out when it is a
 * peer. @p out may be NULL.
 */
oal_controller_who_t oal_discovery_controller(oal_peer_t *out);

#ifdef __cplusplus
}
#endif
