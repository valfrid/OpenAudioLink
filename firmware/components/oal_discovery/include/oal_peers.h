#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * The peer table: who else is on the network, as heard from their
 * announcements (decision 9).
 *
 * Deliberately free of ESP-IDF headers, like oal_rtp_stats, so the slot
 * selection and staleness arithmetic can be tested on a host. Getting
 * either wrong would show up as a Controller that cannot see a speaker —
 * a routing problem that is really a bookkeeping problem, and one that is
 * miserable to diagnose on hardware.
 *
 * Locking belongs to the caller. This table has no idea what a task is.
 */

/** More than a party system will hold, and small enough to keep static. */
#define OAL_MAX_PEERS 16

/**
 * How long silence means gone. Six missed announces at the 5 s interval,
 * matching the Hub's window: multicast is neither acknowledged nor
 * retransmitted on Wi-Fi, so a tighter window makes a healthy node flap
 * between present and absent.
 */
#define OAL_PEER_STALE_MS 30000

/*
 * A Hub identity is "oal-" plus a 36-character GUID: forty characters, so
 * forty-one bytes with the terminator. At forty the last character was
 * being cut off, which two Hubs differing only in their final character
 * would have collapsed into one peer.
 */
#define OAL_PEER_ID_MAX      48
#define OAL_PEER_NAME_MAX    32
#define OAL_PEER_ADDRESS_MAX 16

typedef struct {
    char id[OAL_PEER_ID_MAX];
    char name[OAL_PEER_NAME_MAX];
    uint32_t roles;              /* oal_roles_t bitmask */
    char address[OAL_PEER_ADDRESS_MAX]; /* dotted quad */
    uint16_t control_port;

    /**
     * True when this peer took the Controller role because nobody else had
     * it, rather than being configured to hold it (decision 9). Carried in
     * the announce so precedence can be computed without asking anyone.
     */
    bool claimed;

    int64_t last_seen_us;
} oal_peer_t;

typedef struct {
    oal_peer_t entries[OAL_MAX_PEERS];
    size_t count;
} oal_peer_table_t;

void oal_peer_table_reset(oal_peer_table_t *table);

/**
 * Records an announcement, replacing any earlier one from the same id.
 * Matching on id rather than address is what makes a node whose address
 * changed under DHCP the same node rather than a second one.
 *
 * A full table evicts whichever peer was heard from longest ago, that
 * being the one most likely already gone.
 *
 * @return true when a new peer was added, false when one was updated.
 */
bool oal_peer_table_record(oal_peer_table_t *table, const oal_peer_t *peer);

/**
 * Copies peers heard from within OAL_PEER_STALE_MS of @p now_us into
 * @p out, newest first, and returns how many were written.
 *
 * Staleness is decided here rather than swept by a timer, so a peer is
 * never briefly both expired and listed.
 */
size_t oal_peer_table_live(const oal_peer_table_t *table, int64_t now_us,
                           oal_peer_t *out, size_t max);

/** How many peers are live. Cheaper than copying the table to count it. */
size_t oal_peer_table_count_live(const oal_peer_table_t *table, int64_t now_us);

#ifdef __cplusplus
}
#endif
