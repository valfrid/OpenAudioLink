/*
 * Host tests for the peer table.
 *
 * The table has no ESP-IDF dependencies precisely so this can run
 * anywhere. What it decides — which node is still present, which slot a
 * new one takes, whether a re-announce updates or duplicates — surfaces on
 * hardware as a Controller that cannot route to a speaker, which is a
 * miserable thing to debug through a serial log. Built and run by CI with
 * plain cc.
 */

#include "oal_peers.h"

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

#define CHECK_EQ(actual, expected)                                              \
    do {                                                                        \
        unsigned long a_ = (unsigned long)(actual);                             \
        unsigned long e_ = (unsigned long)(expected);                           \
        if (a_ != e_) {                                                         \
            printf("  FAIL %s:%d: %s was %lu, expected %lu\n",                  \
                   current_test, __LINE__, #actual, a_, e_);                    \
            failures++;                                                         \
        }                                                                       \
    } while (0)

#define CHECK_STR(actual, expected)                                             \
    do {                                                                        \
        if (strcmp((actual), (expected)) != 0) {                                \
            printf("  FAIL %s:%d: %s was \"%s\", expected \"%s\"\n",            \
                   current_test, __LINE__, #actual, (actual), (expected));      \
            failures++;                                                         \
        }                                                                       \
    } while (0)

#define TEST(name) current_test = name; printf("%s\n", name);

#define SECOND 1000000LL

static oal_peer_t make_peer(const char *id, const char *address, int64_t seen_us)
{
    oal_peer_t peer = { 0 };
    snprintf(peer.id, sizeof(peer.id), "%s", id);
    snprintf(peer.name, sizeof(peer.name), "node-%s", id);
    snprintf(peer.address, sizeof(peer.address), "%s", address);
    peer.roles = 1u; /* consumer */
    peer.control_port = 41001;
    peer.last_seen_us = seen_us;
    return peer;
}

static void a_new_peer_is_added(void)
{
    TEST("a new peer is added");
    oal_peer_table_t table;
    oal_peer_table_reset(&table);

    oal_peer_t peer = make_peer("mac-a", "192.168.0.10", 1 * SECOND);
    CHECK(oal_peer_table_record(&table, &peer));
    CHECK_EQ(table.count, 1);
    CHECK_EQ(oal_peer_table_count_live(&table, 1 * SECOND), 1);
}

/*
 * A node announces every five seconds forever. If each announcement added
 * a row the table would be full of one device inside two minutes.
 */
static void re_announcing_updates_rather_than_duplicates(void)
{
    TEST("re-announcing updates rather than duplicates");
    oal_peer_table_t table;
    oal_peer_table_reset(&table);

    oal_peer_t first = make_peer("mac-a", "192.168.0.10", 1 * SECOND);
    oal_peer_t again = make_peer("mac-a", "192.168.0.10", 6 * SECOND);
    CHECK(oal_peer_table_record(&table, &first));
    CHECK(!oal_peer_table_record(&table, &again));

    CHECK_EQ(table.count, 1);
    CHECK_EQ(table.entries[0].last_seen_us, 6 * SECOND);
}

/*
 * DHCP moves a node's address; identity is what says it is the same node.
 * Keying on address would leave the old one in the table, still counted as
 * live for thirty seconds, and route audio to nobody.
 */
static void a_moved_peer_keeps_one_row(void)
{
    TEST("a peer that changed address keeps one row");
    oal_peer_table_t table;
    oal_peer_table_reset(&table);

    oal_peer_t before = make_peer("mac-a", "192.168.0.10", 1 * SECOND);
    oal_peer_t after = make_peer("mac-a", "192.168.0.99", 7 * SECOND);
    oal_peer_table_record(&table, &before);
    oal_peer_table_record(&table, &after);

    CHECK_EQ(table.count, 1);
    CHECK_STR(table.entries[0].address, "192.168.0.99");
}

static void silence_expires_a_peer(void)
{
    TEST("silence expires a peer");
    oal_peer_table_t table;
    oal_peer_table_reset(&table);

    oal_peer_t peer = make_peer("mac-a", "192.168.0.10", 10 * SECOND);
    oal_peer_table_record(&table, &peer);

    /* Still inside the window at exactly the boundary, gone past it. */
    CHECK_EQ(oal_peer_table_count_live(&table, 40 * SECOND), 1);
    CHECK_EQ(oal_peer_table_count_live(&table, 41 * SECOND), 0);

    /* Expiring does not remove the row, so a peer that comes back reuses
     * it rather than consuming another slot. */
    CHECK_EQ(table.count, 1);
}

static void an_expired_peer_is_not_listed(void)
{
    TEST("an expired peer is not listed");
    oal_peer_table_t table;
    oal_peer_table_reset(&table);

    oal_peer_t old_peer = make_peer("mac-a", "192.168.0.10", 1 * SECOND);
    oal_peer_t new_peer = make_peer("mac-b", "192.168.0.11", 60 * SECOND);
    oal_peer_table_record(&table, &old_peer);
    oal_peer_table_record(&table, &new_peer);

    oal_peer_t out[OAL_MAX_PEERS];
    CHECK_EQ(oal_peer_table_live(&table, 60 * SECOND, out, OAL_MAX_PEERS), 1);
    CHECK_STR(out[0].id, "mac-b");
}

static void newest_is_listed_first(void)
{
    TEST("newest is listed first");
    oal_peer_table_t table;
    oal_peer_table_reset(&table);

    oal_peer_t a = make_peer("mac-a", "192.168.0.10", 5 * SECOND);
    oal_peer_t b = make_peer("mac-b", "192.168.0.11", 9 * SECOND);
    oal_peer_t c = make_peer("mac-c", "192.168.0.12", 7 * SECOND);
    oal_peer_table_record(&table, &a);
    oal_peer_table_record(&table, &b);
    oal_peer_table_record(&table, &c);

    oal_peer_t out[OAL_MAX_PEERS];
    CHECK_EQ(oal_peer_table_live(&table, 10 * SECOND, out, OAL_MAX_PEERS), 3);
    CHECK_STR(out[0].id, "mac-b");
    CHECK_STR(out[1].id, "mac-c");
    CHECK_STR(out[2].id, "mac-a");
}

/*
 * A table full of live peers must still accept a new one, and the peer
 * heard from longest ago is the one most likely already gone.
 */
static void a_full_table_evicts_the_stalest(void)
{
    TEST("a full table evicts the stalest");
    oal_peer_table_t table;
    oal_peer_table_reset(&table);

    for (size_t i = 0; i < OAL_MAX_PEERS; i++) {
        char id[OAL_PEER_ID_MAX];
        snprintf(id, sizeof(id), "mac-%02u", (unsigned)i);
        /* Slot 3 is the oldest; the rest are progressively newer. */
        oal_peer_t peer = make_peer(id, "192.168.0.10",
                                    (i == 3 ? 1 : (int64_t)i + 10) * SECOND);
        oal_peer_table_record(&table, &peer);
    }
    CHECK_EQ(table.count, OAL_MAX_PEERS);

    oal_peer_t newcomer = make_peer("mac-new", "192.168.0.99", 30 * SECOND);
    CHECK(oal_peer_table_record(&table, &newcomer));

    CHECK_EQ(table.count, OAL_MAX_PEERS);
    CHECK_STR(table.entries[3].id, "mac-new");
}

/*
 * The caller's buffer decides how many come back. Writing past it would
 * corrupt whatever the control server had on its stack.
 */
static void the_output_buffer_is_respected(void)
{
    TEST("the output buffer is respected");
    oal_peer_table_t table;
    oal_peer_table_reset(&table);

    for (size_t i = 0; i < 5; i++) {
        char id[OAL_PEER_ID_MAX];
        snprintf(id, sizeof(id), "mac-%02u", (unsigned)i);
        oal_peer_t peer = make_peer(id, "192.168.0.10", (int64_t)(i + 1) * SECOND);
        oal_peer_table_record(&table, &peer);
    }

    oal_peer_t out[2] = { 0 };
    CHECK_EQ(oal_peer_table_live(&table, 6 * SECOND, out, 2), 2);
}

static void null_arguments_are_survivable(void)
{
    TEST("null arguments are survivable");
    oal_peer_t out[OAL_MAX_PEERS];
    oal_peer_t peer = make_peer("mac-a", "192.168.0.10", SECOND);

    oal_peer_table_reset(NULL);
    CHECK(!oal_peer_table_record(NULL, &peer));
    CHECK_EQ(oal_peer_table_live(NULL, SECOND, out, OAL_MAX_PEERS), 0);
    CHECK_EQ(oal_peer_table_count_live(NULL, SECOND), 0);

    oal_peer_table_t table;
    oal_peer_table_reset(&table);
    CHECK(!oal_peer_table_record(&table, NULL));

    /* An announcement without an id is not a peer; recording it would put
     * an empty-keyed row in the table that every later lookup collides
     * with. */
    oal_peer_t anonymous = { 0 };
    anonymous.last_seen_us = SECOND;
    CHECK(!oal_peer_table_record(&table, &anonymous));
    CHECK_EQ(table.count, 0);
}

/*
 * A Hub announces "oal-" plus a 36-character GUID — forty characters, which
 * needs forty-one bytes. Storing it in forty silently dropped the last one,
 * and two Hubs differing only there would have become one peer.
 */
static void a_full_length_hub_id_survives(void)
{
    TEST("a full length hub id survives");
    oal_peer_table_t table;
    oal_peer_table_reset(&table);

    const char *hub = "oal-6be61cdb-366a-46c9-a001-c2c37982ada3";
    CHECK(strlen(hub) == 40);

    oal_peer_t peer = make_peer(hub, "192.168.0.66", 1 * SECOND);
    CHECK_STR(peer.id, hub);

    oal_peer_table_record(&table, &peer);
    CHECK_STR(table.entries[0].id, hub);

    /* And an id differing only in that last character stays a second peer. */
    oal_peer_t other = make_peer("oal-6be61cdb-366a-46c9-a001-c2c37982ada4",
                                 "192.168.0.67", 2 * SECOND);
    CHECK(oal_peer_table_record(&table, &other));
    CHECK_EQ(table.count, 2);
}

int main(void)
{
    a_full_length_hub_id_survives();
    a_new_peer_is_added();
    re_announcing_updates_rather_than_duplicates();
    a_moved_peer_keeps_one_row();
    silence_expires_a_peer();
    an_expired_peer_is_not_listed();
    newest_is_listed_first();
    a_full_table_evicts_the_stalest();
    the_output_buffer_is_respected();
    null_arguments_are_survivable();

    if (failures > 0) {
        printf("\n%d check(s) failed\n", failures);
        return 1;
    }
    printf("\nall peer table checks passed\n");
    return 0;
}
