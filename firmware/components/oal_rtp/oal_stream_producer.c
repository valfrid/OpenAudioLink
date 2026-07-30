#include "oal_stream.h"

#include <string.h>

#include "esp_log.h"
#include "esp_random.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "lwip/sockets.h"

static const char *TAG = "oal_producer";

static oal_stream_request_t s_request;
static oal_stream_producer_state_t s_state;
static TaskHandle_t s_task;
static volatile bool s_stop;

/*
 * Pacing is against a monotonic clock, not a task delay. At 200 packets a
 * second a FreeRTOS tick is 2 ticks per packet, and accumulating rounding
 * error would drift the stream against its own media clock — which is the
 * one thing a receiver measures. Timestamps advance by exactly one packet
 * of frames regardless of when the packet actually left, so the RTP clock
 * stays the media clock and jitter stays a property of the network.
 */
static void producer_task(void *arg)
{
    int sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_IP);
    if (sock < 0) {
        ESP_LOGE(TAG, "socket() failed: errno %d", errno);
        s_state.running = false;
        s_task = NULL;
        vTaskDelete(NULL);
        return;
    }

    struct sockaddr_in destinations[OAL_STREAM_MAX_DESTINATIONS] = { 0 };
    for (size_t i = 0; i < s_request.destination_count; i++) {
        destinations[i].sin_family = AF_INET;
        destinations[i].sin_port = htons(s_request.port);
        destinations[i].sin_addr.s_addr = inet_addr(s_request.destinations[i]);
    }

    static uint8_t packet[OAL_RTP_PACKET_BYTES];
    oal_rtp_header_t header = {
        .version = OAL_RTP_VERSION,
        .payload_type = OAL_RTP_PAYLOAD_TYPE,
        .sequence = (uint16_t)esp_random(),
        .timestamp = esp_random(),
        .ssrc = esp_random(),
        .marker = true, /* first packet of the stream */
    };

    const int64_t packet_us = OAL_RTP_PTIME_MS * 1000;
    int64_t next_send_us = esp_timer_get_time();
    s_state.started_at_us = (uint64_t)next_send_us;

    ESP_LOGI(TAG, "streaming to %u destination(s) on port %u, ssrc %08x",
             (unsigned)s_request.destination_count, s_request.port, (unsigned)header.ssrc);

    while (!s_stop) {
        oal_rtp_header_write(&header, packet, sizeof(packet));
        oal_rtp_fill_payload(packet + OAL_RTP_HEADER_BYTES, s_request.source,
                             header.timestamp, s_request.tone_hz);

        /* One packet, replicated byte-identically. Every consumer must see
         * the same sequence numbers and timestamps, or their measurements
         * are of different streams and cannot be compared. */
        for (size_t i = 0; i < s_request.destination_count; i++) {
            if (sendto(sock, packet, sizeof(packet), 0,
                       (struct sockaddr *)&destinations[i], sizeof(destinations[i])) < 0) {
                s_state.send_errors++;
            } else {
                s_state.datagrams_sent++;
            }
        }
        s_state.packets_sent++;

        header.sequence++;
        header.timestamp += OAL_RTP_FRAMES_PER_PACKET;
        header.marker = false;

        next_send_us += packet_us;
        int64_t now = esp_timer_get_time();
        int64_t sleep_us = next_send_us - now;

        if (sleep_us < -packet_us) {
            /* Further behind than a whole packet: the radio or another task
             * took the CPU. Catching up by bursting would only make the
             * jitter worse, so give up the lost time and count it. */
            s_state.late_packets++;
            next_send_us = now;
            sleep_us = 0;
        }

        if (sleep_us > 0) {
            /* vTaskDelay rounds to whole ticks, so sleep the whole ticks
             * and spin the remainder — a few hundred microseconds at most,
             * and only when the tick rate cannot express the interval. */
            int64_t ticks = sleep_us / (1000000 / configTICK_RATE_HZ);
            if (ticks > 0) {
                vTaskDelay((TickType_t)ticks);
            }
            while (esp_timer_get_time() < next_send_us) {
                /* spin */
            }
        }
    }

    close(sock);
    ESP_LOGI(TAG, "stream stopped after %u packets", (unsigned)s_state.packets_sent);
    s_state.running = false;
    s_task = NULL;
    vTaskDelete(NULL);
}

esp_err_t oal_stream_producer_start(const oal_stream_request_t *request)
{
    if (request == NULL || request->destination_count == 0
        || request->destination_count > OAL_STREAM_MAX_DESTINATIONS) {
        return ESP_ERR_INVALID_ARG;
    }

    oal_stream_producer_stop();

    s_request = *request;
    if (s_request.port == 0) {
        s_request.port = OAL_RTP_DEFAULT_PORT;
    }
    if (s_request.tone_hz == 0) {
        s_request.tone_hz = 1000;
    }

    memset(&s_state, 0, sizeof(s_state));
    s_state.running = true;
    s_state.destination_count = s_request.destination_count;
    s_state.port = s_request.port;
    s_state.source = s_request.source;
    s_stop = false;

    /* Above the default task priority: missing a send window shows up as
     * jitter at every consumer, and there is nothing else on this node
     * that matters more while a stream is running. */
    if (xTaskCreate(producer_task, "oal_producer", 4096, NULL, 6, &s_task) != pdPASS) {
        s_state.running = false;
        return ESP_ERR_NO_MEM;
    }
    return ESP_OK;
}

void oal_stream_producer_stop(void)
{
    if (s_task == NULL) {
        return;
    }
    s_stop = true;
    /* The task closes its own socket and clears the handle; waiting keeps
     * a restart from binding a second sender alongside the first. */
    for (int i = 0; i < 100 && s_task != NULL; i++) {
        vTaskDelay(pdMS_TO_TICKS(10));
    }
}

void oal_stream_producer_get(oal_stream_producer_state_t *out)
{
    if (out != NULL) {
        *out = s_state;
    }
}
