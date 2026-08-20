#pragma once

#include <stddef.h>
#include <stdint.h>

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

/**
 * How many times this node has landed on a different access point than the
 * one it was on before.
 *
 * Zero is what a stationary speaker should report. Anything else is the
 * explanation for audio that stops for a second or two at a time and for a
 * producer and its consumer ending up on different radios — which in a mesh
 * is completely invisible, because every access point advertises the same
 * SSID and the node insists it is connected throughout.
 */
uint32_t oal_wifi_roams(void);

/**
 * Scan for access points and describe what was found, as JSON.
 *
 * The diagnostic this component was missing. A mesh advertises one SSID
 * from several radios, and `/status` reports only the one the node landed
 * on — so "why is it on that access point" has never been answerable from
 * the node itself. It was answered once by carrying the board around the
 * house and once by cupping a hand over the antenna, which is not a method.
 *
 * @warning A scan costs the connection for a second or two. Every packet
 *          due in that window is lost, so this is a command an operator
 *          runs deliberately, never something polled.
 *
 * @return bytes written, or -1 if the buffer is too small or the scan
 *         failed.
 */
int oal_wifi_scan_json(char *out, size_t out_size);

/** Longest output `oal_wifi_scan_json` can produce, with the terminator. */
#define OAL_WIFI_SCAN_JSON_MAX 1024

/**
 * Forget which access point we were on and join again from a fresh scan.
 *
 * The other half of the same gap. The sticky rule deliberately refuses to
 * look elsewhere while the current access point will still have us, which
 * is right for a stationary speaker mid-record and wrong for a board that
 * has just been carried into another room — measured as a node one metre
 * from a new access point holding on to one twenty metres away, through
 * reboots and a power cycle.
 *
 * Nothing here overrides the selection rule; it only clears the memory the
 * rule is sticky *about*, and asks for a scan. What comes back may be the
 * same access point, and that is a real answer rather than a failure.
 */
esp_err_t oal_wifi_rejoin(void);

#ifdef __cplusplus
}
#endif
