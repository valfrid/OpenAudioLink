#pragma once

#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * The set of addresses a Producer is sending to.
 *
 * Separate from the stream, and mutable while one runs, because a Consumer
 * joins by asking rather than by being present when the music started
 * (decision 9). A speaker switched on halfway through a record should start
 * playing, not wait for the stream to be restarted.
 *
 * Free of ESP-IDF headers so the set arithmetic — dedupe, capacity,
 * removal from the middle — can be tested on a host, like oal_rtp_stats
 * and oal_peers.
 */

/** Most consumers one producer will feed; decision 2 expects strain near 4. */
#define OAL_STREAM_MAX_DESTINATIONS 8

/** Longest dotted quad plus terminator. */
#define OAL_ADDRESS_MAX 16

typedef struct {
    char entries[OAL_STREAM_MAX_DESTINATIONS][OAL_ADDRESS_MAX];
    size_t count;
} oal_destinations_t;

typedef enum {
    OAL_DEST_ADDED,
    OAL_DEST_ALREADY_PRESENT,
    OAL_DEST_FULL,
    OAL_DEST_INVALID,
} oal_destinations_result_t;

/**
 * True for a dotted quad and nothing else.
 *
 * This is not pedantry. inet_addr() answers INADDR_NONE for anything it
 * cannot parse, and INADDR_NONE is 255.255.255.255 — so a typo in an
 * address would not fail, it would quietly aim two hundred packets a
 * second at the broadcast address. Rejecting the input is the only place
 * that can be caught cheaply.
 *
 * Leading zeros are refused because inet_addr reads them as octal, which
 * makes "010.0.0.1" a different host from the one that was typed.
 */
bool oal_address_is_ipv4(const char *address);

void oal_destinations_reset(oal_destinations_t *set);

bool oal_destinations_contains(const oal_destinations_t *set, const char *address);

/**
 * Adds an address. Idempotent: a Consumer that asks to join twice — which
 * it will, since asking is how it recovers from a lost reply — must not
 * end up receiving two copies of every packet.
 */
oal_destinations_result_t oal_destinations_add(oal_destinations_t *set, const char *address);

/** Removes an address. Returns false when it was not there. */
bool oal_destinations_remove(oal_destinations_t *set, const char *address);

#ifdef __cplusplus
}
#endif
