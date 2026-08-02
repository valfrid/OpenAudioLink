#pragma once

#include <stdbool.h>
#include <stdint.h>

#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * A Consumer telling the Controller it is ready (decision 9).
 *
 * The Consumer initiates and the Controller decides. That order matters:
 * a Producer that scanned for speakers could start sending to one still
 * booting, and the packets would be lost before anything was wrong. A
 * Consumer speaks when it is actually ready.
 *
 * The behaviour never depends on which kind of Controller answers. A
 * turntable adds it to the stream; a Hub, which knows about rooms and
 * knows nobody pressed play, says stand by. Same request, same code path,
 * different answer.
 */

/** How often to ask before the first answer. */
#define OAL_JOIN_RETRY_MS 5000

/**
 * How often to ask again afterwards. Repeating rather than asking once is
 * what heals a Controller that restarted and lost its destination list:
 * the Consumer simply asks again, and adding is idempotent so nothing is
 * duplicated by the asking.
 */
#define OAL_JOIN_REFRESH_MS 30000

/**
 * Starts the join task. Only meaningful on a node holding Consumer.
 *
 * @param id       this node's identity, as announced
 * @param rtp_port the port this node is listening for audio on
 */
esp_err_t oal_join_start(const char *id, uint16_t rtp_port);

/** True once a Controller has answered at least once. */
bool oal_join_acknowledged(void);

/** The last answer a Controller gave: "playing", "standby", or "". */
const char *oal_join_last_status(void);

#ifdef __cplusplus
}
#endif
