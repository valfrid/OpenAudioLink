#include "oal_election.h"

#include <string.h>

/* Mirrors oal_config.h without including it: that header pulls in esp_err.h
 * and this file is deliberately buildable on a host. The values are fixed
 * by the wire protocol, so they cannot drift apart silently. */
#define ROLE_CONSUMER   (1u << 0)
#define ROLE_PRODUCER   (1u << 1)
#define ROLE_CONTROLLER (1u << 2)

/* Lower is better. A configured Controller outranks one that merely took
 * the role because nobody else had it. */
#define RANK_CONFIGURED 0
#define RANK_CLAIMED    1
#define RANK_NONE       2

static unsigned rank_of(uint32_t roles, bool claimed)
{
    if ((roles & ROLE_CONTROLLER) != 0) {
        return claimed ? RANK_CLAIMED : RANK_CONFIGURED;
    }
    /* Not a Controller today, but a Producer may become one. A node that
     * only consumes never claims: it would have nothing to route. */
    return (roles & ROLE_PRODUCER) != 0 ? RANK_CLAIMED : RANK_NONE;
}

oal_election_result_t oal_election_run(
    const char *my_id, uint32_t my_roles,
    const oal_election_peer_t *peers, size_t peer_count,
    size_t *winner)
{
    if (my_id == NULL) {
        return OAL_ELECTION_NONE;
    }

    /* Not claimed: CONTROLLER in my_roles means configured, and a node
     * that only has PRODUCER falls through to the eligible-claimer rank
     * anyway. Passing true here ranked a configured Controller below one
     * that had merely taken the role. */
    unsigned best_rank = rank_of(my_roles, false);
    const char *best_id = my_id;
    bool best_is_self = true;
    size_t best_index = 0;

    for (size_t i = 0; i < peer_count; i++) {
        const oal_election_peer_t *peer = &peers[i];
        if (peer == NULL || peer->id == NULL || peer->id[0] == '\0') {
            continue;
        }
        /* Only peers that already hold the role are candidates. A peer that
         * is merely eligible has not claimed, and guessing that it might is
         * how two nodes end up waiting for each other. */
        if ((peer->roles & ROLE_CONTROLLER) == 0) {
            continue;
        }

        unsigned rank = rank_of(peer->roles, peer->claimed);
        if (rank > best_rank) {
            continue;
        }
        if (rank == best_rank && strcmp(peer->id, best_id) >= 0) {
            continue;
        }

        best_rank = rank;
        best_id = peer->id;
        best_is_self = false;
        best_index = i;
    }

    if (best_rank == RANK_NONE) {
        return OAL_ELECTION_NONE;
    }
    if (best_is_self) {
        return OAL_ELECTION_SELF;
    }
    if (winner != NULL) {
        *winner = best_index;
    }
    return OAL_ELECTION_PEER;
}
