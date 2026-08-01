/*
 * Host tests for the Consumer's channel profile (decision 10).
 *
 * A single mono or one-side speaker is a first-class case for this
 * project, so the arithmetic that produces it is worth proving before any
 * DAC exists. A sum that clips or a sign extension that wraps is audible
 * damage a listener would blame on the network. Built and run by CI with
 * plain cc.
 */

#include "oal_channel.h"

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
        long a_ = (long)(actual);                                               \
        long e_ = (long)(expected);                                             \
        if (a_ != e_) {                                                         \
            printf("  FAIL %s:%d: %s was %ld, expected %ld\n",                  \
                   current_test, __LINE__, #actual, a_, e_);                    \
            failures++;                                                         \
        }                                                                       \
    } while (0)

#define TEST(name) current_test = name; printf("%s\n", name);

#define FULL_POSITIVE  8388607   /* 2^23 - 1 */
#define FULL_NEGATIVE (-8388608) /* -2^23 */

static void put(uint8_t *slot, int32_t value)
{
    uint32_t raw = (uint32_t)value & 0xFFFFFFu;
    slot[0] = (uint8_t)(raw >> 16);
    slot[1] = (uint8_t)(raw >> 8);
    slot[2] = (uint8_t)raw;
}

static int32_t get(const uint8_t *slot)
{
    int32_t value = ((int32_t)slot[0] << 16) | ((int32_t)slot[1] << 8) | slot[2];
    return (value & 0x800000) != 0 ? value - 0x1000000 : value;
}

/** One frame: left in slot 0, right in slot 1. */
static void frame(uint8_t *payload, int32_t left, int32_t right)
{
    put(payload, left);
    put(payload + 3, right);
}

static void names_round_trip(void)
{
    TEST("names round trip");
    oal_channel_t parsed;
    CHECK(oal_channel_parse("stereo", &parsed) && parsed == OAL_CHANNEL_STEREO);
    CHECK(oal_channel_parse("mono", &parsed) && parsed == OAL_CHANNEL_MONO);
    CHECK(oal_channel_parse("left", &parsed) && parsed == OAL_CHANNEL_LEFT);
    CHECK(oal_channel_parse("right", &parsed) && parsed == OAL_CHANNEL_RIGHT);

    CHECK(strcmp(oal_channel_name(OAL_CHANNEL_STEREO), "stereo") == 0);
    CHECK(strcmp(oal_channel_name(OAL_CHANNEL_MONO), "mono") == 0);
    CHECK(strcmp(oal_channel_name(OAL_CHANNEL_LEFT), "left") == 0);
    CHECK(strcmp(oal_channel_name(OAL_CHANNEL_RIGHT), "right") == 0);
}

/*
 * Falling back to stereo on a typo would give a mono speaker both channels
 * and look like a wiring fault rather than a configuration one.
 */
static void an_unknown_name_is_refused(void)
{
    TEST("an unknown name is refused");
    oal_channel_t parsed = OAL_CHANNEL_MONO;
    CHECK(!oal_channel_parse("Left", &parsed));   /* case matters */
    CHECK(!oal_channel_parse("l", &parsed));
    CHECK(!oal_channel_parse("", &parsed));
    CHECK(!oal_channel_parse(NULL, &parsed));
    CHECK(!oal_channel_parse("stereo ", &parsed));
    CHECK_EQ(parsed, OAL_CHANNEL_MONO);           /* untouched on failure */
}

static void stereo_changes_nothing(void)
{
    TEST("stereo changes nothing");
    uint8_t payload[6];
    frame(payload, 1000, -2000);
    oal_channel_apply(payload, 1, OAL_CHANNEL_STEREO);

    CHECK_EQ(get(payload), 1000);
    CHECK_EQ(get(payload + 3), -2000);
}

/*
 * Both slots carry the chosen signal, so a node drives one speaker or two
 * identical ones and no assembly can wire the silent output.
 */
static void one_side_lands_in_both_slots(void)
{
    TEST("one side lands in both slots");
    uint8_t payload[6];

    frame(payload, 1000, -2000);
    oal_channel_apply(payload, 1, OAL_CHANNEL_LEFT);
    CHECK_EQ(get(payload), 1000);
    CHECK_EQ(get(payload + 3), 1000);

    frame(payload, 1000, -2000);
    oal_channel_apply(payload, 1, OAL_CHANNEL_RIGHT);
    CHECK_EQ(get(payload), -2000);
    CHECK_EQ(get(payload + 3), -2000);
}

static void mono_halves_the_sum(void)
{
    TEST("mono halves the sum");
    uint8_t payload[6];

    frame(payload, 1000, 3000);
    oal_channel_apply(payload, 1, OAL_CHANNEL_MONO);
    CHECK_EQ(get(payload), 2000);
    CHECK_EQ(get(payload + 3), 2000);

    /* Equal and opposite cancels, as it must. */
    frame(payload, 5000, -5000);
    oal_channel_apply(payload, 1, OAL_CHANNEL_MONO);
    CHECK_EQ(get(payload), 0);
}

/*
 * The reason for halving. Summing two channels at full scale would wrap
 * to a large negative value — the loudest possible passage turning into a
 * crack, which is exactly when a listener is paying attention.
 */
static void mono_does_not_clip_at_full_scale(void)
{
    TEST("mono does not clip at full scale");
    uint8_t payload[6];

    frame(payload, FULL_POSITIVE, FULL_POSITIVE);
    oal_channel_apply(payload, 1, OAL_CHANNEL_MONO);
    CHECK_EQ(get(payload), FULL_POSITIVE);
    CHECK_EQ(get(payload + 3), FULL_POSITIVE);

    frame(payload, FULL_NEGATIVE, FULL_NEGATIVE);
    oal_channel_apply(payload, 1, OAL_CHANNEL_MONO);
    CHECK_EQ(get(payload), FULL_NEGATIVE);
    CHECK_EQ(get(payload + 3), FULL_NEGATIVE);
}

/*
 * Sign extension is where a 24-bit format goes wrong: read as unsigned,
 * a quiet negative sample becomes a very loud positive one.
 */
static void negative_samples_survive_the_round_trip(void)
{
    TEST("negative samples survive the round trip");
    uint8_t payload[6];
    const int32_t values[] = { -1, -2, -8388608, -4194304, -100000 };

    for (size_t i = 0; i < sizeof(values) / sizeof(values[0]); i++) {
        frame(payload, values[i], values[i]);
        oal_channel_apply(payload, 1, OAL_CHANNEL_MONO);
        CHECK_EQ(get(payload), values[i]);
        CHECK_EQ(get(payload + 3), values[i]);
    }
}

static void every_frame_is_converted(void)
{
    TEST("every frame is converted");
    uint8_t payload[6 * 4];
    for (size_t i = 0; i < 4; i++) {
        frame(payload + i * 6, (int32_t)(i + 1) * 100, -(int32_t)(i + 1) * 100);
    }

    oal_channel_apply(payload, 4, OAL_CHANNEL_LEFT);
    for (size_t i = 0; i < 4; i++) {
        CHECK_EQ(get(payload + i * 6), (int32_t)(i + 1) * 100);
        CHECK_EQ(get(payload + i * 6 + 3), (int32_t)(i + 1) * 100);
    }
}

static void nothing_is_touched_beyond_the_frames_given(void)
{
    TEST("nothing is touched beyond the frames given");
    uint8_t payload[6 * 2];
    frame(payload, 1000, 2000);
    frame(payload + 6, 3000, 4000);

    oal_channel_apply(payload, 1, OAL_CHANNEL_LEFT);

    CHECK_EQ(get(payload + 6), 3000);
    CHECK_EQ(get(payload + 9), 4000);
}

static void null_and_empty_are_survivable(void)
{
    TEST("null and empty are survivable");
    uint8_t payload[6];
    frame(payload, 1000, 2000);

    oal_channel_apply(NULL, 1, OAL_CHANNEL_MONO);
    oal_channel_apply(payload, 0, OAL_CHANNEL_MONO);

    CHECK_EQ(get(payload), 1000);
    CHECK_EQ(get(payload + 3), 2000);
}

int main(void)
{
    names_round_trip();
    an_unknown_name_is_refused();
    stereo_changes_nothing();
    one_side_lands_in_both_slots();
    mono_halves_the_sum();
    mono_does_not_clip_at_full_scale();
    negative_samples_survive_the_round_trip();
    every_frame_is_converted();
    nothing_is_touched_beyond_the_frames_given();
    null_and_empty_are_survivable();

    if (failures > 0) {
        printf("\n%d check(s) failed\n", failures);
        return 1;
    }
    printf("\nall channel profile checks passed\n");
    return 0;
}
