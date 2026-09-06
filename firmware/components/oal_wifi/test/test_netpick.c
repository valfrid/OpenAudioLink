/*
 * Which network a node joins, checked on the host.
 *
 * These rules run once, at boot, in a room where nobody has a laptop. A
 * mistake in them is not a glitch — it is a speaker that never appears,
 * with no way to ask it why. So the arithmetic is ESP-free and the cases
 * that matter are pinned here rather than discovered at a party.
 *
 * The two that carry the safety argument are
 * An_open_access_point_is_never_the_group and
 * A_setup_portal_is_never_joined. Everything else about this design rests
 * on the passphrase being what proves membership, and those are the two
 * situations where a passphrase cannot refuse anything.
 */

#include "oal_netpick.h"

#include <assert.h>
#include <stdio.h>
#include <string.h>

static int failures;

#define CHECK(cond, ...) do { \
    if (!(cond)) { \
        printf("FAIL %s:%d: ", __func__, __LINE__); \
        printf(__VA_ARGS__); \
        printf("\n"); \
        failures++; \
    } \
} while (0)

#define HOME "Kastanjen"
#define PARTY "oal-party"
#define KEYED false          /* `open` is false: the access point has a key */
#define OPEN true

static oal_netpick_ap_t ap(const char *ssid, int rssi, bool open)
{
    oal_netpick_ap_t a = { .rssi = (int8_t)rssi, .open = open };
    strncpy(a.ssid, ssid, sizeof(a.ssid) - 1);
    return a;
}

/** The plan, with the usual stored pair. */
static size_t plan(const oal_netpick_ap_t *seen, size_t count,
                   oal_netpick_step_t *out)
{
    return oal_netpick_plan(seen, count, HOME, PARTY, true,
                            out, OAL_NETPICK_MAX_PLAN);
}

static bool step_is(const oal_netpick_step_t *s, const char *ssid,
                    oal_netpick_kind_t kind)
{
    return strcmp(s->ssid, ssid) == 0 && s->kind == kind;
}

/* ---------- at home ---------- */

static void At_home_the_first_step_is_home(void)
{
    oal_netpick_ap_t seen[] = {
        ap("Neighbour-5G", -70, KEYED),
        ap(HOME, -48, KEYED),
    };
    oal_netpick_step_t out[OAL_NETPICK_MAX_PLAN];
    size_t n = plan(seen, 2, out);

    CHECK(n == 1, "expected one step, got %zu", n);
    CHECK(n >= 1 && step_is(&out[0], HOME, OAL_NETPICK_HOME),
          "expected the provisioned network first");
}

/*
 * The saving that pays for the scan.
 *
 * Before this, a node at a venue spent its thirty seconds failing to find
 * a network that is miles away, because the only way to learn that was to
 * try. A scan says so in two seconds, and the plan simply does not contain
 * a network nobody is beaconing.
 */
static void A_network_that_is_not_there_is_not_tried(void)
{
    oal_netpick_ap_t seen[] = { ap(PARTY, -55, KEYED) };
    oal_netpick_step_t out[OAL_NETPICK_MAX_PLAN];
    size_t n = plan(seen, 1, out);

    CHECK(n == 2, "expected two steps, got %zu", n);
    CHECK(n >= 1 && step_is(&out[0], PARTY, OAL_NETPICK_PARTY),
          "expected the party network, got \"%s\"", n >= 1 ? out[0].ssid : "");
    /* Home is absent from the scan, so it survives as the blind tail —
     * behind the network that is actually in the room, where it costs
     * nothing unless the party network refuses us. */
    CHECK(n >= 2 && out[1].kind == OAL_NETPICK_HOME_UNSEEN,
          "expected the blind home attempt behind it");
}

/*
 * Unless it might be hidden.
 *
 * A scan does not report a hidden SSID, so "not seen" and "not there" are
 * the same observation and the expensive attempt has to be made anyway —
 * last, once everything cheaper has failed.
 */
static void A_hidden_home_network_is_still_tried_last(void)
{
    oal_netpick_ap_t seen[] = { ap("Neighbour-5G", -70, KEYED) };
    oal_netpick_step_t out[OAL_NETPICK_MAX_PLAN];
    size_t n = plan(seen, 1, out);

    CHECK(n == 1, "expected one step, got %zu", n);
    CHECK(n >= 1 && step_is(&out[0], HOME, OAL_NETPICK_HOME_UNSEEN),
          "expected a blind attempt at the provisioned network");
}

static void An_unprovisioned_node_has_nothing_to_plan(void)
{
    oal_netpick_ap_t seen[] = { ap("Neighbour-5G", -70, KEYED) };
    oal_netpick_step_t out[OAL_NETPICK_MAX_PLAN];
    size_t n = oal_netpick_plan(seen, 1, "", "", false, out, OAL_NETPICK_MAX_PLAN);

    CHECK(n == 0, "expected no steps, got %zu — the portal is the answer here", n);
}

/* ---------- at the venue ---------- */

/*
 * The property the whole design is for.
 *
 * Decision 19 recorded "only one access point is active at a time" as an
 * operator's rule with nothing enforcing it, and described what happens
 * when it is broken: speakers split by whichever beacon is stronger. A
 * fixed order means both being up is a preference rather than a split —
 * every node walks this list and lands in the same place, and the stronger
 * signal does not get a vote.
 */
static void A_phone_and_a_node_do_not_split_the_room(void)
{
    /* The node's access point is much the stronger of the two. */
    oal_netpick_ap_t seen[] = {
        ap("oal-vinyl-A1B2C3", -40, KEYED),
        ap("oal-phone", -72, KEYED),
    };
    oal_netpick_step_t out[OAL_NETPICK_MAX_PLAN];
    size_t n = plan(seen, 2, out);

    CHECK(n == 3, "expected three steps, got %zu", n);
    CHECK(n >= 1 && step_is(&out[0], "oal-phone", OAL_NETPICK_PHONE),
          "the phone must win on name, not on signal");
    CHECK(n >= 2 && step_is(&out[1], "oal-vinyl-A1B2C3", OAL_NETPICK_GROUP),
          "the node's access point is the fallback, not the choice");
}

/* Configuration beats convention: the name the Hub agreed goes first. */
static void The_stored_party_name_outranks_the_convention(void)
{
    oal_netpick_ap_t seen[] = {
        ap("oal-phone", -50, KEYED),
        ap(PARTY, -66, KEYED),
    };
    oal_netpick_step_t out[OAL_NETPICK_MAX_PLAN];
    size_t n = plan(seen, 2, out);

    CHECK(n == 3, "expected three steps, got %zu", n);
    CHECK(n >= 1 && step_is(&out[0], PARTY, OAL_NETPICK_PARTY),
          "expected the stored party name first, got \"%s\"", n >= 1 ? out[0].ssid : "");
    CHECK(n >= 2 && step_is(&out[1], "oal-phone", OAL_NETPICK_PHONE),
          "expected the phone second");
    CHECK(n >= 3 && out[2].kind == OAL_NETPICK_HOME_UNSEEN, "and home, blind, last");
}

/* Home first even at a venue, because a friend's Wi-Fi is the good case:
 * everything already works there and it is what the nodes know. */
static void A_friends_network_is_taken_before_the_party_one(void)
{
    oal_netpick_ap_t seen[] = {
        ap("oal-phone", -45, KEYED),
        ap(HOME, -70, KEYED),
    };
    oal_netpick_step_t out[OAL_NETPICK_MAX_PLAN];
    size_t n = plan(seen, 2, out);

    CHECK(n >= 1 && step_is(&out[0], HOME, OAL_NETPICK_HOME),
          "expected the provisioned network first");
}

static void Several_group_access_points_are_ordered_by_signal(void)
{
    oal_netpick_ap_t seen[] = {
        ap("oal-kitchen", -80, KEYED),
        ap("oal-garden", -52, KEYED),
        ap("oal-shed", -66, KEYED),
    };
    oal_netpick_step_t out[OAL_NETPICK_MAX_PLAN];
    size_t n = plan(seen, 3, out);

    CHECK(n == 4, "expected four steps, got %zu", n);
    CHECK(n >= 1 && step_is(&out[0], "oal-garden", OAL_NETPICK_GROUP), "strongest first");
    CHECK(n >= 2 && step_is(&out[1], "oal-shed", OAL_NETPICK_GROUP), "then the middle one");
    CHECK(n >= 3 && step_is(&out[2], "oal-kitchen", OAL_NETPICK_GROUP), "then the weakest");
    CHECK(n >= 4 && out[3].kind == OAL_NETPICK_HOME_UNSEEN, "and home, blind, last");
}

/* ---------- the two that a passphrase cannot refuse ---------- */

/*
 * The name is a hint; the key is the membership.
 *
 * A refused association costs one attempt and nothing else, which is why
 * matching a prefix is safe at all. An *open* access point refuses
 * nothing — it would take the node in whatever passphrase was offered —
 * so the name would become the credential. Anybody within radio range
 * could then take a room's speakers by naming a hotspot.
 */
static void An_open_access_point_is_never_the_group(void)
{
    oal_netpick_ap_t seen[] = {
        ap("oal-phone", -45, OPEN),
        ap("oal-free-music", -50, OPEN),
    };
    oal_netpick_step_t out[OAL_NETPICK_MAX_PLAN];
    size_t n = plan(seen, 2, out);

    CHECK(n == 1, "expected only the blind home attempt, got %zu steps", n);
    CHECK(n >= 1 && out[0].kind == OAL_NETPICK_HOME_UNSEEN,
          "an open access point must not be joined by name alone");
}

/* Not even when it wears the exact name the Hub stored. */
static void An_open_impostor_of_the_party_name_is_refused(void)
{
    oal_netpick_ap_t seen[] = { ap(PARTY, -40, OPEN) };
    oal_netpick_step_t out[OAL_NETPICK_MAX_PLAN];
    size_t n = plan(seen, 1, out);

    CHECK(n == 1 && out[0].kind == OAL_NETPICK_HOME_UNSEEN,
          "an open access point using the party name must still be refused");
}

/*
 * A node's own provisioning portal is open, and has to be: a person joins
 * it with a phone to type credentials in. Two unprovisioned nodes finding
 * each other would make a network with a settings page at one end and a
 * speaker at the other, reachable from nothing.
 *
 * Today's portal name cannot match the group prefix anyway. This is the
 * test that fails if somebody renames it.
 */
static void A_setup_portal_is_never_joined(void)
{
    oal_netpick_ap_t seen[] = {
        ap("OpenAudioLink-A1B2C3", -38, OPEN),
        /* and the same, renamed into the group's namespace */
        ap("oal-A1B2C3", -38, OPEN),
    };
    oal_netpick_step_t out[OAL_NETPICK_MAX_PLAN];
    size_t n = plan(seen, 2, out);

    CHECK(n == 1 && out[0].kind == OAL_NETPICK_HOME_UNSEEN,
          "a portal must never be a join candidate, under any name");
}

/* ---------- the details that bite ---------- */

/*
 * A phone beacons on two bands and a mesh has several radios, so one name
 * arrives several times. Trying it twice wastes the one thing this design
 * is spending: the seconds before the music starts.
 */
static void One_network_is_planned_once(void)
{
    oal_netpick_ap_t seen[] = {
        ap("oal-phone", -70, KEYED),
        ap("oal-phone", -44, KEYED),
        ap(HOME, -60, KEYED),
        ap(HOME, -51, KEYED),
    };
    oal_netpick_step_t out[OAL_NETPICK_MAX_PLAN];
    size_t n = plan(seen, 4, out);

    CHECK(n == 2, "expected two steps, got %zu", n);
    CHECK(n >= 1 && step_is(&out[0], HOME, OAL_NETPICK_HOME), "home once");
    CHECK(n >= 2 && step_is(&out[1], "oal-phone", OAL_NETPICK_PHONE), "the phone once");
}

/* A person types the hotspot name into a phone, so case is not a promise. */
static void The_convention_name_is_matched_whatever_the_case(void)
{
    oal_netpick_ap_t seen[] = { ap("OAL-Phone", -50, KEYED) };
    oal_netpick_step_t out[OAL_NETPICK_MAX_PLAN];
    size_t n = plan(seen, 1, out);

    CHECK(n >= 1 && out[0].kind == OAL_NETPICK_PHONE,
          "expected \"OAL-Phone\" to be recognised as the hotspot");
    CHECK(n >= 1 && strcmp(out[0].ssid, "OAL-Phone") == 0,
          "and joined under the name that was actually beaconing");
}

/*
 * Configuration is matched exactly, though. Two households on one street
 * may run networks differing only in case, and joining the wrong one is
 * not a near miss.
 */
static void Stored_names_are_matched_exactly(void)
{
    oal_netpick_ap_t seen[] = { ap("kastanjen", -45, KEYED) };
    oal_netpick_step_t out[OAL_NETPICK_MAX_PLAN];
    size_t n = plan(seen, 1, out);

    CHECK(n == 1 && out[0].kind == OAL_NETPICK_HOME_UNSEEN,
          "\"kastanjen\" is not \"" HOME "\"");
}

/* Without a key there is nothing to join a group with, so it is not
 * attempted — a node that never went to a party stays a home speaker. */
static void Without_a_party_key_the_group_is_not_attempted(void)
{
    oal_netpick_ap_t seen[] = {
        ap("oal-phone", -45, KEYED),
        ap(PARTY, -50, KEYED),
    };
    oal_netpick_step_t out[OAL_NETPICK_MAX_PLAN];
    size_t n = oal_netpick_plan(seen, 2, HOME, "", false, out, OAL_NETPICK_MAX_PLAN);

    CHECK(n == 1 && out[0].kind == OAL_NETPICK_HOME_UNSEEN,
          "expected only the blind home attempt, got %zu steps", n);
}

static void The_plan_never_overruns_its_buffer(void)
{
    oal_netpick_ap_t seen[8];
    char names[8][16];
    for (size_t i = 0; i < 8; i++) {
        snprintf(names[i], sizeof(names[i]), "oal-%zu", i);
        seen[i] = ap(names[i], -40 - (int)i, KEYED);
    }
    oal_netpick_step_t out[2];
    size_t n = oal_netpick_plan(seen, 8, HOME, PARTY, true, out, 2);

    CHECK(n == 2, "expected the plan capped at two, got %zu", n);
    CHECK(n >= 1 && strcmp(out[0].ssid, "oal-0") == 0, "strongest still first");
}

static void An_empty_scan_leaves_only_the_blind_attempt(void)
{
    oal_netpick_step_t out[OAL_NETPICK_MAX_PLAN];
    size_t n = oal_netpick_plan(NULL, 0, HOME, PARTY, true, out, OAL_NETPICK_MAX_PLAN);

    CHECK(n == 1 && out[0].kind == OAL_NETPICK_HOME_UNSEEN,
          "expected one blind attempt, got %zu steps", n);
}

static void The_right_key_goes_with_each_step(void)
{
    CHECK(!oal_netpick_uses_party_key(OAL_NETPICK_HOME), "home uses the home key");
    CHECK(!oal_netpick_uses_party_key(OAL_NETPICK_HOME_UNSEEN), "so does the blind attempt");
    CHECK(oal_netpick_uses_party_key(OAL_NETPICK_PARTY), "the party network uses the party key");
    CHECK(oal_netpick_uses_party_key(OAL_NETPICK_PHONE), "so does the phone");
    CHECK(oal_netpick_uses_party_key(OAL_NETPICK_GROUP), "and so does any other group point");
}

int main(void)
{
    At_home_the_first_step_is_home();
    A_network_that_is_not_there_is_not_tried();
    A_hidden_home_network_is_still_tried_last();
    An_unprovisioned_node_has_nothing_to_plan();

    A_phone_and_a_node_do_not_split_the_room();
    The_stored_party_name_outranks_the_convention();
    A_friends_network_is_taken_before_the_party_one();
    Several_group_access_points_are_ordered_by_signal();

    An_open_access_point_is_never_the_group();
    An_open_impostor_of_the_party_name_is_refused();
    A_setup_portal_is_never_joined();

    One_network_is_planned_once();
    The_convention_name_is_matched_whatever_the_case();
    Stored_names_are_matched_exactly();
    Without_a_party_key_the_group_is_not_attempted();
    The_plan_never_overruns_its_buffer();
    An_empty_scan_leaves_only_the_blind_attempt();
    The_right_key_goes_with_each_step();

    if (failures != 0) {
        printf("%d check(s) failed\n", failures);
        return 1;
    }
    printf("network choice: all checks passed\n");
    return 0;
}
