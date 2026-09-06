#include "oal_netpick.h"

#include <string.h>

/** Lower-case ASCII, which is all an SSID convention needs. */
static char fold(char c)
{
    return (c >= 'A' && c <= 'Z') ? (char)(c - 'A' + 'a') : c;
}

static bool same_folded(const char *a, const char *b)
{
    size_t i = 0;
    for (; a[i] != '\0' && b[i] != '\0'; i++) {
        if (fold(a[i]) != fold(b[i])) {
            return false;
        }
    }
    return a[i] == '\0' && b[i] == '\0';
}

static bool starts_with_folded(const char *s, const char *prefix)
{
    for (size_t i = 0; prefix[i] != '\0'; i++) {
        if (s[i] == '\0' || fold(s[i]) != fold(prefix[i])) {
            return false;
        }
    }
    return true;
}

/**
 * Could this access point be offering the group network?
 *
 * Name and encryption only. Whether it really is one is settled by the
 * passphrase at association time, which is the point: this function only
 * has to be cheap and refuse the two cases that a key cannot refuse -- an
 * open access point, and our own portal.
 */
static bool could_be_group(const oal_netpick_ap_t *ap)
{
    if (ap->open) {
        return false;
    }
    if (starts_with_folded(ap->ssid, OAL_NETPICK_PORTAL_PREFIX)) {
        return false;
    }
    return starts_with_folded(ap->ssid, OAL_NETPICK_GROUP_PREFIX);
}

/** The strongest record for @p ssid, or NULL. Exact match. */
static const oal_netpick_ap_t *find_exact(const oal_netpick_ap_t *seen, size_t count,
                                          const char *ssid)
{
    const oal_netpick_ap_t *best = NULL;
    if (ssid == NULL || ssid[0] == '\0') {
        return NULL;
    }
    for (size_t i = 0; i < count; i++) {
        if (strcmp(seen[i].ssid, ssid) != 0) {
            continue;
        }
        if (best == NULL || seen[i].rssi > best->rssi) {
            best = &seen[i];
        }
    }
    return best;
}

static bool already_planned(const oal_netpick_step_t *out, size_t used, const char *ssid)
{
    for (size_t i = 0; i < used; i++) {
        if (strcmp(out[i].ssid, ssid) == 0) {
            return true;
        }
    }
    return false;
}

static size_t add_step(oal_netpick_step_t *out, size_t used, size_t out_max,
                       const char *ssid, oal_netpick_kind_t kind)
{
    if (used >= out_max || ssid == NULL || ssid[0] == '\0') {
        return used;
    }
    if (already_planned(out, used, ssid)) {
        return used;
    }
    /* The caller's buffers are the same width, and a scan record is
     * terminated, so this cannot truncate a name that fits an SSID. */
    strncpy(out[used].ssid, ssid, OAL_NETPICK_SSID_MAX - 1);
    out[used].ssid[OAL_NETPICK_SSID_MAX - 1] = '\0';
    out[used].kind = kind;
    return used + 1;
}

size_t oal_netpick_plan(const oal_netpick_ap_t *seen, size_t seen_count,
                        const char *home_ssid, const char *party_ssid,
                        bool have_party_key,
                        oal_netpick_step_t *out, size_t out_max)
{
    if (out == NULL || out_max == 0) {
        return 0;
    }
    if (seen == NULL) {
        seen_count = 0;
    }

    size_t used = 0;

    /*
     * 1. Home, if the scan saw it.
     *
     * No encryption test. A home network is matched by exactly the name
     * that was provisioned, and if somebody runs an open one that is their
     * arrangement to make -- unlike the group rules below, where the name
     * is a convention anybody could adopt.
     */
    const oal_netpick_ap_t *home = find_exact(seen, seen_count, home_ssid);
    if (home != NULL) {
        used = add_step(out, used, out_max, home->ssid, OAL_NETPICK_HOME);
    }

    if (have_party_key) {
        /* 2. The party network under the name the Hub agreed with every
         * node in the group. Explicit configuration outranks convention,
         * so this goes before the well-known name below. */
        const oal_netpick_ap_t *party = find_exact(seen, seen_count, party_ssid);
        if (party != NULL && !party->open) {
            used = add_step(out, used, out_max, party->ssid, OAL_NETPICK_PARTY);
        }

        /*
         * 3. A phone's hotspot, by the one name every node knows.
         *
         * This is what makes the order deterministic rather than a
         * question of signal strength: if a phone and a node are both
         * beaconing, every speaker in the room walks this same list and
         * lands on the same access point. Two access points then stop
         * being a split and become a preference.
         */
        for (size_t i = 0; i < seen_count; i++) {
            if (could_be_group(&seen[i])
                    && same_folded(seen[i].ssid, OAL_NETPICK_PHONE_SSID)) {
                used = add_step(out, used, out_max, seen[i].ssid, OAL_NETPICK_PHONE);
                break;
            }
        }

        /* 4. Anything else offering the group, strongest first. A node
         * hosting the party names itself here. */
        for (;;) {
            const oal_netpick_ap_t *best = NULL;
            for (size_t i = 0; i < seen_count; i++) {
                if (!could_be_group(&seen[i])
                        || already_planned(out, used, seen[i].ssid)) {
                    continue;
                }
                if (best == NULL || seen[i].rssi > best->rssi) {
                    best = &seen[i];
                }
            }
            if (best == NULL || used >= out_max) {
                break;
            }
            used = add_step(out, used, out_max, best->ssid, OAL_NETPICK_GROUP);
        }
    }

    /*
     * 5. Home again, unseen.
     *
     * A scan does not report a hidden SSID, so a node provisioned onto one
     * would never find its own network if the plan held only what was
     * seen. It goes last because it is the expensive step -- the join has
     * to time out to fail -- and everything above it fails in the time an
     * association takes.
     */
    if (home == NULL) {
        used = add_step(out, used, out_max, home_ssid, OAL_NETPICK_HOME_UNSEEN);
    }

    return used;
}

bool oal_netpick_uses_party_key(oal_netpick_kind_t kind)
{
    return kind == OAL_NETPICK_PARTY
        || kind == OAL_NETPICK_PHONE
        || kind == OAL_NETPICK_GROUP;
}

const char *oal_netpick_kind_name(oal_netpick_kind_t kind)
{
    switch (kind) {
    case OAL_NETPICK_HOME:        return "the provisioned network";
    case OAL_NETPICK_PARTY:       return "the group's party network";
    case OAL_NETPICK_PHONE:       return "a phone hotspot";
    case OAL_NETPICK_GROUP:       return "an access point offering the group";
    case OAL_NETPICK_HOME_UNSEEN: return "the provisioned network, unseen";
    }
    return "?";
}
