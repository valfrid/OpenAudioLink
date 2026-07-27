#pragma once

#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Device control/status server per protocol/CONTROL.md: HTTP/JSON on
 * TCP 41001 with GET /status, POST /reboot and POST /ota.
 *
 * The strings must remain valid for the lifetime of the server.
 */
typedef struct {
    const char *id;
    const char *name;
    const char *role;
    const char *hardware_profile;
    const char *firmware_version;
} oal_control_config_t;

esp_err_t oal_control_start(const oal_control_config_t *config);

#ifdef __cplusplus
}
#endif
