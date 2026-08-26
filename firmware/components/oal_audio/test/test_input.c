/*
 * Host tests for the Producer's input stage.
 *
 * The mirror of test_output.c, guarding the same silent failure from the
 * other end: a node that falls back to the wrong input captures nothing and
 * reports no error. Here it is worse than for the output stage, because the
 * two inputs need the ESP in *opposite* clock roles — so picking wrong is
 * not merely the wrong pins, it can put two drivers on one clock line.
 *
 * Built and run by CI with plain cc.
 */

#include "oal_input.h"

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

static void test_names_round_trip(void)
{
    current_test = "names round trip";

    const oal_input_t all[] = { OAL_INPUT_LINE, OAL_INPUT_MIC };
    for (size_t i = 0; i < sizeof(all) / sizeof(all[0]); i++) {
        oal_input_t parsed;
        CHECK(oal_input_parse(oal_input_name(all[i]), &parsed));
        CHECK(parsed == all[i]);
    }
}

static void test_the_wire_names_are_what_the_api_promises(void)
{
    current_test = "wire names";

    /* Spelled out rather than round-tripped: these strings are in the
     * control API and the switchboard, and a rename that only breaks
     * clients would pass a round-trip test. */
    CHECK(strcmp(oal_input_name(OAL_INPUT_LINE), "line") == 0);
    CHECK(strcmp(oal_input_name(OAL_INPUT_MIC), "mic") == 0);
}

static void test_unknown_names_are_refused(void)
{
    current_test = "unknown names";

    oal_input_t parsed = OAL_INPUT_MIC;
    CHECK(!oal_input_parse("microphone", &parsed));
    CHECK(!oal_input_parse("adc", &parsed));
    CHECK(!oal_input_parse("", &parsed));
    CHECK(!oal_input_parse("LINE", &parsed));   /* case matters on the wire */
    /* Refusing must not have written anything. */
    CHECK(parsed == OAL_INPUT_MIC);
}

static void test_null_arguments_are_survivable(void)
{
    current_test = "null arguments";

    oal_input_t parsed;
    CHECK(!oal_input_parse(NULL, &parsed));
    CHECK(!oal_input_parse("line", NULL));
    CHECK(!oal_input_parse(NULL, NULL));
}

static void test_the_default_is_line(void)
{
    current_test = "default";

    /* Not a preference. Every Producer already deployed was configured
     * before this setting existed and reads nothing from NVS; a default of
     * microphone would silence the turntable on the next update. */
    CHECK(OAL_INPUT_DEFAULT == OAL_INPUT_LINE);
    CHECK(strcmp(oal_input_name(OAL_INPUT_DEFAULT), "line") == 0);
}

static void test_only_the_microphone_needs_clocking(void)
{
    current_test = "clock master";

    /* The whole reason the two cannot share pins. The self-clocked PCM1808
     * drives BCK and LRCK itself; asserting them from this end as well puts
     * two drivers on one wire. */
    CHECK(oal_input_is_clock_master(OAL_INPUT_MIC));
    CHECK(!oal_input_is_clock_master(OAL_INPUT_LINE));
}

static void test_descriptions_are_present_and_distinct(void)
{
    current_test = "descriptions";

    const char *line = oal_input_describe(OAL_INPUT_LINE);
    const char *mic = oal_input_describe(OAL_INPUT_MIC);
    CHECK(line != NULL && mic != NULL);
    CHECK(strlen(line) > 0 && strlen(mic) > 0);
    CHECK(strcmp(line, mic) != 0);
}

static void test_out_of_range_falls_back_rather_than_reading_off_the_end(void)
{
    current_test = "out of range";

    /* Reachable if a later firmware stores a value this one does not know,
     * which a rollback makes real. It must name something, not index past
     * the table. */
    const oal_input_t bogus = (oal_input_t)42;
    CHECK(strcmp(oal_input_name(bogus), "line") == 0);
    CHECK(oal_input_describe(bogus) != NULL);
    CHECK(!oal_input_is_clock_master(bogus));
}

int main(void)
{
    test_names_round_trip();
    test_the_wire_names_are_what_the_api_promises();
    test_unknown_names_are_refused();
    test_null_arguments_are_survivable();
    test_the_default_is_line();
    test_only_the_microphone_needs_clocking();
    test_descriptions_are_present_and_distinct();
    test_out_of_range_falls_back_rather_than_reading_off_the_end();

    if (failures > 0) {
        printf("\n%d input stage check(s) failed\n", failures);
        return 1;
    }
    printf("input stage: all checks passed\n");
    return 0;
}
