#include "oal_stream.h"

#include <string.h>

#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "lwip/sockets.h"

static const char *TAG = "oal_consumer";

static oal_stream_consumer_state_t s_state;
static volatile bool s_reset_requested;
static oal_stream_sink_t s_sink;

/*
 * Arrival time is taken in RTP timestamp units — 48 000 ticks a second —
 * because RFC 3550's jitter estimate subtracts one from the other. Feeding
 * it microseconds and sample counts would produce a number with no meaning
 * and no way to notice.
 */
static uint32_t arrival_in_rtp_units(void)
{
    int64_t us = esp_timer_get_time();
    return (uint32_t)((us * OAL_RTP_SAMPLE_RATE) / 1000000);
}

static void consumer_task(void *arg)
{
    uint16_t port = (uint16_t)(uintptr_t)arg;

    int sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_IP);
    if (sock < 0) {
        ESP_LOGE(TAG, "socket() failed: errno %d", errno);
        vTaskDelete(NULL);
        return;
    }

    int reuse = 1;
    setsockopt(sock, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof(reuse));

    /*
     * A receive buffer several packets deep. The default is smaller than a
     * burst that arrives while this task is descheduled, and the drops
     * would be counted as network loss — measuring our own scheduling and
     * calling it Wi-Fi.
     *
     * Which is exactly what run 28 caught it doing. Two nodes on one access
     * point, same second: the dongle at −48 dBm lost 3 500 ppm while the
     * speaker at −52 dBm lost *nothing*. Better signal, more loss. Not
     * radio — the busier node dropping packets it had already received.
     *
     * The size below was never in force. lwIP only compiles SO_RCVBUF in
     * when `CONFIG_LWIP_SO_RCVBUF` is set, and this build did not set it,
     * so the option was rejected and nobody looked: the return value was
     * discarded. The real ceiling was `CONFIG_LWIP_UDP_RECVMBOX_SIZE`,
     * which is a handful of packets — tens of milliseconds.
     *
     * Both are set in `sdkconfig.defaults` now, and the result is logged
     * rather than assumed. Two nodes have been blamed on radio for wanting
     * a number this file believed it had already asked for.
     */
    int rx_buffer = OAL_RTP_PACKET_BYTES * 64;
    if (setsockopt(sock, SOL_SOCKET, SO_RCVBUF, &rx_buffer, sizeof(rx_buffer)) < 0) {
        ESP_LOGW(TAG, "SO_RCVBUF rejected (errno %d) — receive depth is "
                      "CONFIG_LWIP_UDP_RECVMBOX_SIZE alone", errno);
    } else {
        int actual = 0;
        socklen_t len = sizeof(actual);
        if (getsockopt(sock, SOL_SOCKET, SO_RCVBUF, &actual, &len) == 0) {
            ESP_LOGI(TAG, "receive buffer %d bytes, %d packets, %d ms",
                     actual, actual / OAL_RTP_PACKET_BYTES,
                     actual / OAL_RTP_PACKET_BYTES * 1000
                         / (OAL_RTP_SAMPLE_RATE / OAL_RTP_FRAMES_PER_PACKET));
        }
    }

    struct sockaddr_in bind_addr = {
        .sin_family = AF_INET,
        .sin_port = htons(port),
        .sin_addr.s_addr = htonl(INADDR_ANY),
    };
    if (bind(sock, (struct sockaddr *)&bind_addr, sizeof(bind_addr)) < 0) {
        ESP_LOGE(TAG, "bind(%u) failed: errno %d", port, errno);
        close(sock);
        vTaskDelete(NULL);
        return;
    }

    s_state.listening = true;
    s_state.port = port;
    ESP_LOGI(TAG, "listening for RTP on port %u", port);

    static uint8_t packet[OAL_RTP_PACKET_BYTES];
    for (;;) {
        int len = recvfrom(sock, packet, sizeof(packet), 0, NULL, NULL);
        if (len < 0) {
            vTaskDelay(pdMS_TO_TICKS(10));
            continue;
        }

        if (s_reset_requested) {
            oal_rtp_stats_reset(&s_state.stats);
            s_state.payload_errors = 0;
            s_state.foreign_packets = 0;
            s_reset_requested = false;
        }

        oal_rtp_header_t header;
        if (!oal_rtp_header_read(packet, (size_t)len, &header)) {
            s_state.foreign_packets++;
            continue;
        }

        uint32_t arrival = arrival_in_rtp_units();
        bool usable = oal_rtp_stats_on_packet(
            &s_state.stats, header.sequence, header.timestamp, arrival, header.ssrc);

        s_state.last_ssrc = header.ssrc;
        s_state.last_packet_us = (uint64_t)esp_timer_get_time();

        /* Verify only what would have been played. A duplicate or a packet
         * too late to use is not a payload error, and counting it as one
         * would confuse a network problem with a corruption problem. */
        if (usable) {
            s_state.payload_errors += oal_rtp_verify_payload(
                packet + OAL_RTP_HEADER_BYTES, (size_t)len - OAL_RTP_HEADER_BYTES,
                header.timestamp);

            /* Only what would have been played reaches the speaker. A
             * duplicate or a packet too late to use has already been
             * counted; playing it as well would put the audio out of order
             * to fix a statistic. */
            oal_stream_sink_t sink = s_sink;
            if (sink != NULL) {
                uint32_t frames = oal_rtp_frames_in((size_t)len);
                if (frames > 0) {
                    sink(packet + OAL_RTP_HEADER_BYTES, frames);
                }
            }
        }
    }
}

void oal_stream_consumer_set_sink(oal_stream_sink_t sink)
{
    s_sink = sink;
}

esp_err_t oal_stream_consumer_start(uint16_t port)
{
    if (s_state.listening) {
        return ESP_OK;
    }

    memset(&s_state, 0, sizeof(s_state));
    oal_rtp_stats_reset(&s_state.stats);

    if (port == 0) {
        port = OAL_RTP_DEFAULT_PORT;
    }

    /* Higher than the discovery and control tasks: a packet not taken from
     * the socket in time is indistinguishable, later, from one the network
     * lost. */
    if (xTaskCreate(consumer_task, "oal_consumer", 4096, (void *)(uintptr_t)port, 6, NULL)
            != pdPASS) {
        return ESP_ERR_NO_MEM;
    }
    return ESP_OK;
}

void oal_stream_consumer_get(oal_stream_consumer_state_t *out)
{
    if (out != NULL) {
        *out = s_state;
    }
}

void oal_stream_consumer_reset(void)
{
    /* Cleared by the receive task rather than here, so a reset cannot land
     * halfway through accounting for a packet. */
    s_reset_requested = true;
}
