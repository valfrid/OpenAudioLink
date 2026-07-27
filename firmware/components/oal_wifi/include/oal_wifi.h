#pragma once

#include <stddef.h>
#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    OAL_WIFI_STA,     /* connected to the configured network */
    OAL_WIFI_PORTAL,  /* running the setup access point + provisioning portal */
} oal_wifi_result_t;

/**
 * Brings the network up. Credentials are read from NVS; when NVS has none,
 * the fallback build-time credentials are used if non-empty.
 *
 * Returns OAL_WIFI_STA once the station has an IP address. When no
 * credentials exist, or the network cannot be joined after repeated
 * attempts, the device opens an unprotected setup access point named
 * "OpenAudioLink-XXXXXX" with a provisioning page at http://192.168.4.1/
 * and returns OAL_WIFI_PORTAL. Saving credentials there reboots the device.
 */
oal_wifi_result_t oal_wifi_start(const char *fallback_ssid, const char *fallback_password);

/** Persists Wi-Fi credentials in NVS (namespace "oal"). */
esp_err_t oal_wifi_set_credentials(const char *ssid, const char *password);

#ifdef __cplusplus
}
#endif
