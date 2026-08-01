/*
 * Host tests for the Producer's destination set.
 *
 * Free of ESP-IDF headers so this runs anywhere. The address check is the
 * reason it matters most: inet_addr() answers INADDR_NONE for anything it
 * cannot parse, and INADDR_NONE is 255.255.255.255, so a typed address
 * that gets past here does not fail — it aims two hundred packets a second
 * at the broadcast address. Built and run by CI with plain cc.
 */

#include "oal_destinations.h"

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

#define TEST(name) current_test = name; printf("%s\n", name);

static void valid_addresses_are_accepted(void)
{
    TEST("valid addresses are accepted");
    CHECK(oal_address_is_ipv4("0.0.0.0"));
    CHECK(oal_address_is_ipv4("1.2.3.4"));
    CHECK(oal_address_is_ipv4("192.168.0.235"));
    CHECK(oal_address_is_ipv4("255.255.255.255"));
}

/*
 * Everything here would otherwise become INADDR_NONE and be sent to the
 * broadcast address.
 */
static void anything_else_is_refused(void)
{
    TEST("anything else is refused");
    CHECK(!oal_address_is_ipv4(NULL));
    CHECK(!oal_address_is_ipv4(""));
    CHECK(!oal_address_is_ipv4("192.168.0"));          /* too few octets */
    CHECK(!oal_address_is_ipv4("192.168.0.1.5"));      /* too many */
    CHECK(!oal_address_is_ipv4("192.168.0.256"));      /* out of range */
    CHECK(!oal_address_is_ipv4("192.168.0.1 "));       /* trailing space */
    CHECK(!oal_address_is_ipv4(" 192.168.0.1"));       /* leading space */
    CHECK(!oal_address_is_ipv4("192.168.0."));         /* trailing dot */
    CHECK(!oal_address_is_ipv4("192.168..1"));         /* empty octet */
    CHECK(!oal_address_is_ipv4("192.168.0.1a"));       /* trailing junk */
    CHECK(!oal_address_is_ipv4("-1.0.0.1"));
    CHECK(!oal_address_is_ipv4("kitchen.local"));      /* a hostname is not this */
    CHECK(!oal_address_is_ipv4("1234.1.1.1"));         /* four digits */
}

/*
 * inet_addr reads a leading zero as octal, so "010.0.0.1" is host 8, not
 * host 10 — a difference nothing downstream would report.
 */
static void leading_zeros_are_refused(void)
{
    TEST("leading zeros are refused");
    CHECK(!oal_address_is_ipv4("010.0.0.1"));
    CHECK(!oal_address_is_ipv4("192.168.00.1"));
    CHECK(oal_address_is_ipv4("192.168.0.1"));  /* a bare zero is fine */
}

static void adding_and_removing(void)
{
    TEST("adding and removing");
    oal_destinations_t set;
    oal_destinations_reset(&set);

    CHECK_EQ(oal_destinations_add(&set, "192.168.0.71"), OAL_DEST_ADDED);
    CHECK_EQ(set.count, 1);
    CHECK(oal_destinations_contains(&set, "192.168.0.71"));

    CHECK(oal_destinations_remove(&set, "192.168.0.71"));
    CHECK_EQ(set.count, 0);
    CHECK(!oal_destinations_contains(&set, "192.168.0.71"));
}

/*
 * A Consumer asks to join, and asks again when the reply is lost — which
 * on this network it sometimes will be. Adding it twice would send it two
 * copies of every packet and double what it costs the air.
 */
static void adding_twice_changes_nothing(void)
{
    TEST("adding twice changes nothing");
    oal_destinations_t set;
    oal_destinations_reset(&set);

    CHECK_EQ(oal_destinations_add(&set, "192.168.0.71"), OAL_DEST_ADDED);
    CHECK_EQ(oal_destinations_add(&set, "192.168.0.71"), OAL_DEST_ALREADY_PRESENT);
    CHECK_EQ(set.count, 1);
}

static void an_invalid_address_is_not_added(void)
{
    TEST("an invalid address is not added");
    oal_destinations_t set;
    oal_destinations_reset(&set);

    CHECK_EQ(oal_destinations_add(&set, "kitchen.local"), OAL_DEST_INVALID);
    CHECK_EQ(oal_destinations_add(&set, NULL), OAL_DEST_INVALID);
    CHECK_EQ(set.count, 0);
}

static void the_set_has_a_ceiling(void)
{
    TEST("the set has a ceiling");
    oal_destinations_t set;
    oal_destinations_reset(&set);

    for (size_t i = 0; i < OAL_STREAM_MAX_DESTINATIONS; i++) {
        char address[OAL_ADDRESS_MAX];
        snprintf(address, sizeof(address), "192.168.0.%u", (unsigned)(i + 10));
        CHECK_EQ(oal_destinations_add(&set, address), OAL_DEST_ADDED);
    }
    CHECK_EQ(set.count, OAL_STREAM_MAX_DESTINATIONS);
    CHECK_EQ(oal_destinations_add(&set, "192.168.0.99"), OAL_DEST_FULL);
    CHECK_EQ(set.count, OAL_STREAM_MAX_DESTINATIONS);
}

/*
 * Removing from the middle is where a set like this goes wrong: leaving a
 * hole would send to an address that is no longer there, and shifting
 * badly would drop somebody who still is.
 */
static void removing_from_the_middle_keeps_everyone_else(void)
{
    TEST("removing from the middle keeps everyone else");
    oal_destinations_t set;
    oal_destinations_reset(&set);

    oal_destinations_add(&set, "192.168.0.10");
    oal_destinations_add(&set, "192.168.0.11");
    oal_destinations_add(&set, "192.168.0.12");

    CHECK(oal_destinations_remove(&set, "192.168.0.11"));
    CHECK_EQ(set.count, 2);
    CHECK(oal_destinations_contains(&set, "192.168.0.10"));
    CHECK(oal_destinations_contains(&set, "192.168.0.12"));
    CHECK(!oal_destinations_contains(&set, "192.168.0.11"));

    /* The vacated slot must be usable again, not left as a gap. */
    CHECK_EQ(oal_destinations_add(&set, "192.168.0.13"), OAL_DEST_ADDED);
    CHECK_EQ(set.count, 3);
    CHECK(oal_destinations_contains(&set, "192.168.0.13"));
}

static void removing_something_absent_is_harmless(void)
{
    TEST("removing something absent is harmless");
    oal_destinations_t set;
    oal_destinations_reset(&set);

    oal_destinations_add(&set, "192.168.0.10");
    CHECK(!oal_destinations_remove(&set, "192.168.0.99"));
    CHECK(!oal_destinations_remove(&set, NULL));
    CHECK(!oal_destinations_remove(NULL, "192.168.0.10"));
    CHECK_EQ(set.count, 1);
}

/*
 * A full set that loses one must accept a newcomer. This is the sequence a
 * Controller performs when moving a speaker between rooms.
 */
static void a_full_set_accepts_a_newcomer_after_a_removal(void)
{
    TEST("a full set accepts a newcomer after a removal");
    oal_destinations_t set;
    oal_destinations_reset(&set);

    for (size_t i = 0; i < OAL_STREAM_MAX_DESTINATIONS; i++) {
        char address[OAL_ADDRESS_MAX];
        snprintf(address, sizeof(address), "192.168.0.%u", (unsigned)(i + 10));
        oal_destinations_add(&set, address);
    }

    CHECK(oal_destinations_remove(&set, "192.168.0.10"));
    CHECK_EQ(oal_destinations_add(&set, "192.168.0.99"), OAL_DEST_ADDED);
    CHECK_EQ(set.count, OAL_STREAM_MAX_DESTINATIONS);
    CHECK(oal_destinations_contains(&set, "192.168.0.99"));
    CHECK(!oal_destinations_contains(&set, "192.168.0.10"));
}

int main(void)
{
    valid_addresses_are_accepted();
    anything_else_is_refused();
    leading_zeros_are_refused();
    adding_and_removing();
    adding_twice_changes_nothing();
    an_invalid_address_is_not_added();
    the_set_has_a_ceiling();
    removing_from_the_middle_keeps_everyone_else();
    removing_something_absent_is_harmless();
    a_full_set_accepts_a_newcomer_after_a_removal();

    if (failures > 0) {
        printf("\n%d check(s) failed\n", failures);
        return 1;
    }
    printf("\nall destination set checks passed\n");
    return 0;
}
