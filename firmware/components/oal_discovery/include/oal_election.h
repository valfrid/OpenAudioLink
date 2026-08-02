#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Who holds the Controller role (decision 9).
 *
 * There is always exactly one Controller, and a Consumer's behaviour never
 * depends on which kind it is: it finds the Controller, says it is ready,
 * and does as it is told. What differs is only the answer — a Hub knows
 * about rooms and says stand by, a turntable says here is the stream.
 *
 * A party system has no PC, so the Producer takes the role. It is the only
 * node guaranteed to exist, and co-hosting turns "tell the Producer where
 * to send" from a network call into a function call.
 *
 * Decided by precedence rather than by an election protocol: every node
 * computes the same answer alone, from announcements already on the wire.
 * No votes, no terms, no split-brain recovery to get wrong.
 *
 * Free of ESP-IDF headers so the ordering can be tested on a host. Two
 * nodes disagreeing about who is Controller is the failure this prevents,
 * and it is not a thing anyone wants to debug from a serial log.
 */

typedef struct {
    const char *id;
    uint32_t roles;   /* oal_roles_t bitmask */

    /**
     * True when this peer took the role because nobody else had it, false
     * when it was configured to hold it. A Hub is configured; a turntable
     * claims. Configured always outranks claimed, so carrying a Hub into
     * a party makes the turntable stand down rather than argue.
     */
    bool claimed;
} oal_election_peer_t;

typedef enum {
    OAL_ELECTION_NONE = 0,  /* nobody holds it and we are not eligible */
    OAL_ELECTION_SELF,      /* we hold it, or should claim it */
    OAL_ELECTION_PEER,      /* a peer holds it */
} oal_election_result_t;

/**
 * Decides who should hold Controller.
 *
 * @param my_id     this node's identity
 * @param my_roles  configured roles. CONTROLLER here means configured, not
 *                  claimed; PRODUCER makes this node eligible to claim.
 * @param peers     peers currently heard from
 * @param winner    set to the winning peer's index when the result is PEER
 *
 * Ordering: a configured Controller beats a claimer, and among equals the
 * lower id wins. Lower id is arbitrary but total and identical everywhere,
 * which is the only property that matters — every node reaches the same
 * answer without exchanging a word about it.
 */
oal_election_result_t oal_election_run(
    const char *my_id, uint32_t my_roles,
    const oal_election_peer_t *peers, size_t peer_count,
    size_t *winner);

#ifdef __cplusplus
}
#endif
