#pragma once

#include <stddef.h>
#include <stdint.h>

#include "esp_err.h"
#include "oal_channel.h"
#include "oal_input.h"
#include "oal_output.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Node configuration held in NVS rather than compiled in (decision 5).
 *
 * Roles are logical, not device-bound (ARCHITECTURE.md section 2), so a
 * node carries a *set* of them: an analog source that also plays is both
 * a producer and a consumer, and that case is the reason this is a mask
 * and not a single value. One firmware image serves every node; what a
 * given board does is configuration, not a separate binary.
 *
 * Controller is defined here because the Hub announces it and a node may
 * take limited controller duties in standalone mode. No node claims it
 * today.
 */
typedef enum {
    OAL_ROLE_NONE       = 0,
    OAL_ROLE_CONSUMER   = 1u << 0, /* receives RTP, jitter buffer, I2S out */
    OAL_ROLE_PRODUCER   = 1u << 1, /* I2S in, packetises, sends RTP */
    OAL_ROLE_CONTROLLER = 1u << 2, /* discovery, selection, route ownership */
} oal_role_t;

typedef uint32_t oal_roles_t;

/** Every role a node may hold, for validating input. */
#define OAL_ROLES_ALL (OAL_ROLE_CONSUMER | OAL_ROLE_PRODUCER | OAL_ROLE_CONTROLLER)

/** Unconfigured nodes are consumers; that is the common case by far. */
#define OAL_ROLES_DEFAULT (OAL_ROLE_CONSUMER)

/** Wire name of a single role, or NULL if the value is not one role. */
const char *oal_role_name(oal_role_t role);

/** Single role by wire name, or OAL_ROLE_NONE if unrecognised. */
oal_role_t oal_role_from_name(const char *name);

/**
 * Parses a comma-separated list such as "producer,consumer".
 * Returns OAL_ROLE_NONE if any element is unrecognised, so a typo is
 * rejected outright rather than silently dropping a role.
 */
oal_roles_t oal_roles_parse(const char *list);

/** Writes `consumer,producer`. Returns length, or -1 if it will not fit. */
int oal_roles_to_list(oal_roles_t roles, char *out, size_t out_size);

/** Writes `["consumer","producer"]`. Returns length, or -1 if it will not fit. */
int oal_roles_to_json(oal_roles_t roles, char *out, size_t out_size);

/** Longest output either formatter can produce, including the terminator. */
#define OAL_ROLES_STR_MAX 64

/** Stored roles, or OAL_ROLES_DEFAULT when unset or unreadable. */
oal_roles_t oal_config_get_roles(void);

/** Persists roles. Rejects OAL_ROLE_NONE and unknown bits. */
esp_err_t oal_config_set_roles(oal_roles_t roles);

/*
 * Which of the stream's two channels this node plays (decision 10). A
 * single mono or one-side speaker is a first-class arrangement here, not
 * a degraded stereo one, so this sits beside the roles rather than being
 * inferred from anything.
 */

/** Stored channel profile, or stereo when unset or unreadable. */
/**
 * Absolute bound on a stored delay, in milliseconds.
 *
 * Not the limit a node will actually accept. That one depends on the ring,
 * which is now a setting, so it is computed at runtime from
 * `oal_playout_max_target_ms()` minus the default target and published in
 * `/status` as `maxDelayMs`. Six hundred and fifty is simply what the
 * largest permitted ring could ever allow (1000 ms ring, 750 ms cap, 100 ms
 * default target) and exists so a value out of NVS is bounded by something.
 *
 * The distinction matters because it has bitten before. This was 50 while
 * the ring was fixed at 200, and 200 while the ring was briefly twice the
 * size -- and the Hub went on offering 0-200 in its dialog for two releases
 * after the real ceiling became 50. A constant that has to be kept in step
 * by hand does not stay in step. Ask the node.
 */
#define OAL_DELAY_MS_MAX 650

/*
 * How much audio the ring can hold, in milliseconds -- capacity, not
 * target. See oal_playout.h for why the two are different things.
 *
 * A setting because the right value is not knowable from here. This project
 * runs a 100 ms target in a 200 ms ring where Snapcast runs 1000 ms; the
 * house it runs in shows 900 ms delivery stalls that no 200 ms ring can
 * absorb. Which size is right is an experiment, and an experiment needs a
 * knob rather than a rebuild -- particularly for the node that is awkward
 * to reach with a cable.
 */
#define OAL_RING_MS_MIN 50u
#define OAL_RING_MS_MAX 1000u
#define OAL_RING_MS_DEFAULT 200u

/** Stored ring size, or OAL_RING_MS_DEFAULT when unset or out of range. */
uint32_t oal_config_get_ring_ms(void);

/**
 * Stores it. Takes effect at the next boot, unlike the delay.
 *
 * The ring is an allocation, so changing it under a running playout would
 * mean freeing the buffer the audio task is reading from. Roles work the
 * same way and for the same reason.
 */
esp_err_t oal_config_set_ring_ms(uint32_t ring_ms);

/**
 * Extra playout delay for this node, in milliseconds, or 0.
 *
 * Corrects a difference *between* nodes rather than a property of the
 * installation: given the same packet, a USB dongle plays tens of
 * milliseconds later than an I²S DAC, so the DAC has to be held back to
 * meet it. Which node needs the trim depends on what is plugged into it.
 *
 * Only ever positive — nothing can play a sample before it arrives, so
 * alignment is always the early node waiting.
 */
uint32_t oal_config_get_delay_ms(void);

/** Stores it. Applied immediately by the caller and again at next boot. */
esp_err_t oal_config_set_delay_ms(uint32_t delay_ms);

oal_channel_t oal_config_get_channel(void);

/** Persists the channel profile. Rejects values outside the enum. */
esp_err_t oal_config_set_channel(oal_channel_t channel);

/*
 * Playback volume, 0-100, per node.
 *
 * Per node and not per stream, because the level a room wants is a
 * property of the room — how far the speaker is from the sofa, how loud
 * the kitchen extractor is — and it should not have to be set again every
 * time the music comes from somewhere else. It survives a reboot for the
 * same reason: a node that came back at full scale after a firmware update
 * would be a genuinely unpleasant surprise at seven in the morning.
 */

/** Stored volume, or 100 when unset or unreadable. */
uint8_t oal_config_get_volume(void);

/** Persists the volume. Rejects anything above 100. */
esp_err_t oal_config_set_volume(uint8_t percent);

/*
 * Which output stage this board has (docs/USB-AUDIO.md).
 *
 * Beside the channel profile and the volume because it answers the same
 * kind of question — what is this particular box — rather than what is
 * playing. A node is soldered to a DAC or has a dongle pushed into it, and
 * one image serves both.
 */

/** Stored output stage, or I2S when unset or unreadable. */
oal_output_t oal_config_get_output(void);

/** Persists the output stage. Rejects values outside the enum. */
esp_err_t oal_config_set_output(oal_output_t output);

/*
 * What this Producer captures from (docs/ROOM-CALIBRATION.md).
 *
 * Beside the output stage because it is the same kind of fact from the
 * other end of the chain, and read at boot for a harder reason: it selects
 * a set of pins *and* which end drives the bit and word clocks. See
 * oal_input.h -- the microphone needs clocking and the self-clocked ADC
 * module supplies its own, so sharing pins would put two drivers on one
 * line.
 */

/** Stored input stage, or line in when unset or unreadable. */
oal_input_t oal_config_get_input(void);

/** Persists the input stage. Rejects values outside the enum. */
esp_err_t oal_config_set_input(oal_input_t input);

/** Longest node name, including the terminator. Matches the announce field. */
#define OAL_NAME_MAX 32

/**
 * Copies the stored node name into @p out.
 * @return ESP_OK, or ESP_ERR_NVS_NOT_FOUND when no name has been set —
 *         in which case the caller supplies its own default.
 */
esp_err_t oal_config_get_name(char *out, size_t out_size);

/** Persists the node name. An empty name clears it, restoring the default. */
esp_err_t oal_config_set_name(const char *name);

#ifdef __cplusplus
}
#endif
