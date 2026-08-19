/*
 * Host tests for the Consumer's output stage.
 *
 * Small, and worth having anyway: this parser is reachable from the
 * provisioning form and the control API, and the failure it guards against
 * is silent. A node that falls back to the wrong output stage plays nothing
 * and reports no error, which is the hardest kind of fault to find — the
 * project has already lost evenings to exactly that shape (run 18, and the
 * FLAC channel that decoded nothing while every counter rose).
 *
 * Built and run by CI with plain cc.
 */

#include "oal_output.h"

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

    const oal_output_t all[] = { OAL_OUTPUT_I2S, OAL_OUTPUT_USB };
    for (size_t i = 0; i < sizeof(all) / sizeof(all[0]); i++) {
        oal_output_t parsed;
        CHECK(oal_output_parse(oal_output_name(all[i]), &parsed));
        CHECK(parsed == all[i]);
    }

    CHECK(strcmp(oal_output_name(OAL_OUTPUT_I2S), "i2s") == 0);
    CHECK(strcmp(oal_output_name(OAL_OUTPUT_USB), "usb") == 0);
}

static void test_unknown_is_refused(void)
{
    current_test = "unknown is refused";

    /* The whole point of the parser returning bool. Falling back to a
     * default here would send audio to a stage the board may not have. */
    oal_output_t parsed = OAL_OUTPUT_USB;
    CHECK(!oal_output_parse("dac", &parsed));
    CHECK(!oal_output_parse("I2S", &parsed));      /* case matters on the wire */
    CHECK(!oal_output_parse("", &parsed));
    CHECK(!oal_output_parse("usb ", &parsed));
    CHECK(parsed == OAL_OUTPUT_USB);               /* untouched on failure */

    CHECK(!oal_output_parse(NULL, &parsed));
    CHECK(!oal_output_parse("usb", NULL));
}

static void test_default_is_i2s(void)
{
    current_test = "default is i2s";

    /* Every node deployed before this setting existed reads nothing from
     * NVS. If the default were USB they would all go silent on the next
     * update, which is why this is asserted rather than assumed. */
    CHECK(OAL_OUTPUT_DEFAULT == OAL_OUTPUT_I2S);

    /* An out-of-range stored value must land somewhere safe too. */
    CHECK(strcmp(oal_output_name((oal_output_t)99), "i2s") == 0);
}

static void test_descriptions_exist(void)
{
    current_test = "descriptions exist";

    CHECK(strlen(oal_output_describe(OAL_OUTPUT_I2S)) > 0);
    CHECK(strlen(oal_output_describe(OAL_OUTPUT_USB)) > 0);
    CHECK(strcmp(oal_output_describe(OAL_OUTPUT_I2S),
                 oal_output_describe(OAL_OUTPUT_USB)) != 0);
}

int main(void)
{
    test_names_round_trip();
    test_unknown_is_refused();
    test_default_is_i2s();
    test_descriptions_exist();

    if (failures > 0) {
        printf("%d check(s) failed\n", failures);
        return 1;
    }
    printf("output stage: all checks passed\n");
    return 0;
}
