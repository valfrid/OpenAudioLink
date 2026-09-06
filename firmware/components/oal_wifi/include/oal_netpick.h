#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/*
 * Which network a node joins, decided from one scan.
 *
 * Deliberately free of ESP-IDF headers. The rules below decide where a
 * speaker spends its evening, and getting them wrong is the kind of fault
 * that only appears at a venue with the guests already there — so they are
 * arithmetic over a list of names, testable on a laptop, rather than
 * something interleaved with the driver.
 *
 * The chain, in order:
 *
 *   1. the network this node was provisioned onto
 *   2. the group's party network, by the exact name the Hub stored
 *   3. a phone's hotspot, by the well-known name
 *   4. any other access point offering the group network
 *   5. the provisioned network again, unseen -- it may be hidden
 *
 * and the provisioning portal if none of them answers.
 *
 * **The name finds the network; the key decides membership.** Steps 2 to 4
 * all join with the one party passphrase the Hub pushed to every node in
 * the group, so a stranger's access point called "oal-anything" costs one
 * refused association and nothing else. That is the whole safety argument,
 * and it is why an open access point is never a candidate for those steps
 * however it is named: an access point with no key cannot prove membership
 * of a group defined by holding one.
 */

/** 32 bytes and a terminator, which is the 802.11 maximum. */
#define OAL_NETPICK_SSID_MAX 33

/** The name a phone's hotspot is set to. Convention, not configuration. */
#define OAL_NETPICK_PHONE_SSID "oal-phone"

/** What an access point offering the group network calls itself. */
#define OAL_NETPICK_GROUP_PREFIX "oal-"

/*
 * A node's own provisioning portal, which must never be joined
 * automatically.
 *
 * It is open by design, because a person with a phone has to be able to
 * reach it to type credentials in. So the key-is-the-gate argument above
 * is exactly the argument that does not hold here, and two unprovisioned
 * nodes finding each other would produce a network with a settings page on
 * one end, a speaker on the other, and no way to reach either -- while
 * occupying one of the four association slots the portal keeps for a
 * person.
 *
 * Today it does not begin with "oal-" and so cannot match anyway. The
 * check is here for the day somebody renames it.
 */
#define OAL_NETPICK_PORTAL_PREFIX "OpenAudioLink-"

/** Enough steps to be worth trying; beyond this the boot is just slow. */
#define OAL_NETPICK_MAX_PLAN 4

/** One access point as the scan reported it. */
typedef struct {
    char ssid[OAL_NETPICK_SSID_MAX];
    int8_t rssi;
    bool open;   /**< no encryption at all */
} oal_netpick_ap_t;

typedef enum {
    OAL_NETPICK_HOME,        /**< the provisioned network, seen */
    OAL_NETPICK_PARTY,       /**< the stored party name, seen */
    OAL_NETPICK_PHONE,       /**< the well-known hotspot name */
    OAL_NETPICK_GROUP,       /**< some other access point offering the group */
    OAL_NETPICK_HOME_UNSEEN, /**< the provisioned network, tried blind */
} oal_netpick_kind_t;

typedef struct {
    char ssid[OAL_NETPICK_SSID_MAX];
    oal_netpick_kind_t kind;
} oal_netpick_step_t;

/**
 * Builds the join plan from what the scan found.
 *
 * @p seen may hold several records for one name -- a mesh, or a phone
 * beaconing on two bands -- and the strongest wins; the plan never names
 * the same network twice.
 *
 * @p home_ssid and @p party_ssid may be empty, meaning not stored. They are
 * matched exactly, because they are configuration and a difference in case
 * is a different network. The convention names above are matched without
 * regard to case, because a person types those into a phone.
 *
 * @p have_party_key says whether a party passphrase is stored. Without one
 * there is nothing to authenticate with, so steps 2 to 4 are skipped
 * entirely rather than attempted and refused.
 *
 * Returns how many steps were written, at most @p out_max.
 */
size_t oal_netpick_plan(const oal_netpick_ap_t *seen, size_t seen_count,
                        const char *home_ssid, const char *party_ssid,
                        bool have_party_key,
                        oal_netpick_step_t *out, size_t out_max);

/** Whether a step joins with the party passphrase rather than the home one. */
bool oal_netpick_uses_party_key(oal_netpick_kind_t kind);

/** A name for the log, so "joining X because Y" reads as one line. */
const char *oal_netpick_kind_name(oal_netpick_kind_t kind);
