/*
 * Host tests for Controller precedence (decision 9).
 *
 * Every node computes this alone and they must all reach the same answer,
 * so the ordering has to be a total order with no ties left over. Two nodes
 * disagreeing about who is Controller means a speaker being told two
 * different things, which is not a thing anyone wants to debug from a
 * serial log. Built and run by CI with plain cc.
 */

#include "oal_election.h"

#include <stdio.h>
#include <string.h>

static int failures;
static const char *current_test;

#define CHECK(expr)                                                        \
    do {                                                                   \
        if (!(expr)) {                                                     \
            printf("  FAIL %s:%d: %s\n", current_test, __LINE__, #expr);   \
            failures++;                                                    \
        }                                                                  \
    } while (0)

#define TEST(name) current_test = name; printf("%s\n", name);

#define CONSUMER   (1u << 0)
#define PRODUCER   (1u << 1)
#define CONTROLLER (1u << 2)

/*
 * One turntable, no PC. Nobody holds the role, so the Producer takes it —
 * this is the case the whole design exists for.
 */
static void a_lone_producer_claims_it(void)
{
    TEST("a lone producer claims it");
    CHECK(oal_election_run("mac-b", PRODUCER, NULL, 0, NULL) == OAL_ELECTION_SELF);
}

/*
 * A speaker has nothing to route, so it never claims. Two speakers alone in
 * a room should both wait, not fight over a role neither can use.
 */
static void a_lone_consumer_claims_nothing(void)
{
    TEST("a lone consumer claims nothing");
    CHECK(oal_election_run("mac-a", CONSUMER, NULL, 0, NULL) == OAL_ELECTION_NONE);

    oal_election_peer_t peers[] = { { "mac-b", CONSUMER, false } };
    CHECK(oal_election_run("mac-a", CONSUMER, peers, 1, NULL) == OAL_ELECTION_NONE);
}

/*
 * The Hub is configured; the turntable claimed. Carrying the turntable into
 * the house must make it stand down rather than argue.
 */
static void a_configured_controller_beats_a_claimer(void)
{
    TEST("a configured controller beats a claimer");
    oal_election_peer_t hub[] = { { "oal-hub", CONTROLLER | PRODUCER, false } };
    size_t winner = 99;

    /* "mac-aaa" sorts below "oal-hub", so this also proves rank is
     * consulted before id — otherwise the turntable would win on name. */
    CHECK(oal_election_run("mac-aaa", PRODUCER, hub, 1, &winner) == OAL_ELECTION_PEER);
    CHECK(winner == 0);
}

/*
 * Lower id is arbitrary but total, which is the only property that matters:
 * both nodes reach the same answer without exchanging a word.
 */
static void two_claimers_are_settled_by_id(void)
{
    TEST("two claimers are settled by id");
    oal_election_peer_t higher[] = { { "mac-z", PRODUCER | CONTROLLER, true } };
    oal_election_peer_t lower[] = { { "mac-a", PRODUCER | CONTROLLER, true } };
    size_t winner = 99;

    CHECK(oal_election_run("mac-m", PRODUCER, higher, 1, &winner) == OAL_ELECTION_SELF);
    CHECK(oal_election_run("mac-m", PRODUCER, lower, 1, &winner) == OAL_ELECTION_PEER);
    CHECK(winner == 0);
}

static void two_configured_controllers_are_settled_by_id(void)
{
    TEST("two configured controllers are settled by id");
    oal_election_peer_t peers[] = { { "oal-aaa", CONTROLLER, false } };
    size_t winner = 99;

    CHECK(oal_election_run("oal-zzz", CONTROLLER, peers, 1, &winner) == OAL_ELECTION_PEER);
    CHECK(winner == 0);
    CHECK(oal_election_run("oal-000", CONTROLLER, peers, 1, &winner) == OAL_ELECTION_SELF);
}

/*
 * A Producer that is merely eligible has not claimed anything. Treating it
 * as a candidate is how two nodes end up each waiting for the other.
 */
static void an_eligible_peer_is_not_a_controller(void)
{
    TEST("an eligible peer is not a controller");
    oal_election_peer_t peers[] = { { "mac-a", PRODUCER, false } };

    /* "mac-a" sorts below us but holds nothing, so we still claim. */
    CHECK(oal_election_run("mac-b", PRODUCER, peers, 1, NULL) == OAL_ELECTION_SELF);
}

/*
 * The party case in full: a turntable, two speakers, and one of the
 * speakers announcing before the turntable has claimed.
 */
static void a_party_settles_on_the_turntable(void)
{
    TEST("a party settles on the turntable");
    oal_election_peer_t speakers[] = {
        { "mac-aaa", CONSUMER, false },
        { "mac-bbb", CONSUMER, false },
    };
    size_t winner = 99;

    /* From the turntable's view, even though both speakers sort lower. */
    CHECK(oal_election_run("mac-zzz", PRODUCER, speakers, 2, &winner) == OAL_ELECTION_SELF);

    /* From a speaker's view, once the turntable has claimed. */
    oal_election_peer_t seen[] = {
        { "mac-bbb", CONSUMER, false },
        { "mac-zzz", PRODUCER | CONTROLLER, true },
    };
    CHECK(oal_election_run("mac-aaa", CONSUMER, seen, 2, &winner) == OAL_ELECTION_PEER);
    CHECK(winner == 1);
}

/*
 * The Hub arriving mid-party. Every node must move to it in the same round,
 * or a speaker ends up taking instructions from two places.
 */
static void a_hub_arriving_takes_over_everywhere(void)
{
    TEST("a hub arriving takes over everywhere");
    oal_election_peer_t from_speaker[] = {
        { "mac-zzz", PRODUCER | CONTROLLER, true },
        { "oal-hub", CONTROLLER | PRODUCER, false },
    };
    oal_election_peer_t from_turntable[] = {
        { "mac-aaa", CONSUMER, false },
        { "oal-hub", CONTROLLER | PRODUCER, false },
    };
    size_t winner = 99;

    CHECK(oal_election_run("mac-aaa", CONSUMER, from_speaker, 2, &winner) == OAL_ELECTION_PEER);
    CHECK(winner == 1);

    CHECK(oal_election_run("mac-zzz", PRODUCER, from_turntable, 2, &winner)
          == OAL_ELECTION_PEER);
    CHECK(winner == 1);
}

static void malformed_peers_are_skipped(void)
{
    TEST("malformed peers are skipped");
    oal_election_peer_t peers[] = {
        { NULL, CONTROLLER, false },
        { "", CONTROLLER, false },
        { "mac-a", CONTROLLER, false },
    };
    size_t winner = 99;

    CHECK(oal_election_run("mac-z", PRODUCER, peers, 3, &winner) == OAL_ELECTION_PEER);
    CHECK(winner == 2);

    CHECK(oal_election_run(NULL, PRODUCER, peers, 3, &winner) == OAL_ELECTION_NONE);
}

/*
 * A node that is Controller, Producer and Consumer at once — the turntable
 * that also plays. It is its own Controller and must not look for another.
 */
static void a_node_holding_everything_is_its_own_controller(void)
{
    TEST("a node holding everything is its own controller");
    CHECK(oal_election_run("mac-a", CONSUMER | PRODUCER | CONTROLLER, NULL, 0, NULL)
          == OAL_ELECTION_SELF);
}

int main(void)
{
    a_lone_producer_claims_it();
    a_lone_consumer_claims_nothing();
    a_configured_controller_beats_a_claimer();
    two_claimers_are_settled_by_id();
    two_configured_controllers_are_settled_by_id();
    an_eligible_peer_is_not_a_controller();
    a_party_settles_on_the_turntable();
    a_hub_arriving_takes_over_everywhere();
    malformed_peers_are_skipped();
    a_node_holding_everything_is_its_own_controller();

    if (failures > 0) {
        printf("\n%d check(s) failed\n", failures);
        return 1;
    }
    printf("\nall election checks passed\n");
    return 0;
}
