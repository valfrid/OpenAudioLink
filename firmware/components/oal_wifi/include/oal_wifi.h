#pragma once

#include <stdbool.h>
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
 * Persists the group's party network — decision 4's standalone mode.
 *
 * Every node in a group holds the same pair, and a node tries it only
 * after the network it was provisioned onto has failed. That ordering is
 * the entire design: at home the fallback never runs because the first
 * attempt succeeds, and at a venue the first cannot succeed, so the same
 * unconditional rule does the right thing in both places. A consumer
 * therefore holds no mode, needs nothing set before an event and nothing
 * cleared after one.
 *
 * An empty @p ssid forgets it, which is how a node leaves a group.
 *
 * Applies at the next boot; a node already on a network is not disturbed.
 */
esp_err_t oal_wifi_set_party(const char *ssid, const char *password);

/**
 * Whether a party network is stored.
 *
 * Deliberately a yes-or-no. The Hub needs to show which nodes are ready
 * for an event, and that question is answered without ever putting a
 * passphrase into a status document that half a dozen things poll.
 */
bool oal_wifi_has_party(void);

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
 * How many times this node has been disconnected since boot, and why the
 * last one happened (an ESP-IDF wifi_err_reason_t; 0 if never).
 *
 * A node that drops off for seconds loses a thousand consecutive packets,
 * which reads as interference from every counter downstream and is not.
 * The reason separates the two cases that matter: 8, ASSOC_LEAVE, is the
 * access point asking it to go -- steering, common where two access points
 * share an SSID -- and 200, BEACON_TIMEOUT, is the node losing the access
 * point. Steering is fixed on the router; a beacon timeout is fixed with
 * an antenna, a position, or a wire.
 */
uint32_t oal_wifi_disconnects(void);
int oal_wifi_last_reason(void);

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
