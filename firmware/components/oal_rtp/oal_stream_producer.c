#include "oal_stream.h"

#include <errno.h>
#include <string.h>

#include "esp_log.h"
#include "esp_random.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "lwip/sockets.h"

static const char *TAG = "oal_producer";

static oal_stream_request_t s_request;
static oal_stream_producer_state_t s_state;
static TaskHandle_t s_task;
static oal_stream_source_t s_source;
static volatile bool s_stop;

/*
 * The destination set, and a counter the send loop watches.
 *
 * The loop cannot take a lock per packet and cannot read the set without
 * one either, so it keeps its own resolved copy and reloads only when the
 * counter moves. Two hundred times a second, the common case is that
 * nothing changed and no lock is touched at all.
 */
static oal_destinations_t s_destinations;
static SemaphoreHandle_t s_destinations_lock;
static volatile uint32_t s_destinations_generation;

static bool ensure_lock(void)
{
    if (s_destinations_lock == NULL) {
        s_destinations_lock = xSemaphoreCreateMutex();
    }
    return s_destinations_lock != NULL;
}

/*
 * Resolves the set into sockaddrs and returns the generation it belongs
 * to. The generation is read inside the lock alongside the copy: read
 * outside, a change landing between the two would leave the loop with a
 * stale list stamped as current, and the new speaker would stay silent
 * until something else changed.
 */
static uint32_t reload_destinations(uint16_t port, struct sockaddr_in *out, size_t *count)
{
    uint32_t generation = 0;
    if (xSemaphoreTake(s_destinations_lock, portMAX_DELAY) != pdTRUE) {
        /* One short of current, so the next pass tries again. Returning the
         * current value would mark a list we never read as up to date. */
        return s_destinations_generation - 1u;
    }

    *count = s_destinations.count;
    for (size_t i = 0; i < s_destinations.count; i++) {
        out[i].sin_family = AF_INET;
        out[i].sin_port = htons(port);
        out[i].sin_addr.s_addr = inet_addr(s_destinations.entries[i]);
    }
    generation = s_destinations_generation;
    s_state.destination_count = s_destinations.count;

    xSemaphoreGive(s_destinations_lock);
    return generation;
}

oal_destinations_result_t oal_stream_producer_add_destination(const char *address)
{
    if (!ensure_lock()) {
        return OAL_DEST_FULL;
    }
    if (xSemaphoreTake(s_destinations_lock, portMAX_DELAY) != pdTRUE) {
        return OAL_DEST_FULL;
    }

    oal_destinations_result_t result = oal_destinations_add(&s_destinations, address);
    if (result == OAL_DEST_ADDED) {
        s_destinations_generation++;
        s_state.destination_count = s_destinations.count;
    }

    xSemaphoreGive(s_destinations_lock);
    return result;
}

bool oal_stream_producer_remove_destination(const char *address)
{
    if (!ensure_lock() || xSemaphoreTake(s_destinations_lock, portMAX_DELAY) != pdTRUE) {
        return false;
    }

    bool removed = oal_destinations_remove(&s_destinations, address);
    if (removed) {
        s_destinations_generation++;
        s_state.destination_count = s_destinations.count;
    }

    xSemaphoreGive(s_destinations_lock);
    return removed;
}

void oal_stream_producer_destinations(oal_destinations_t *out)
{
    if (out == NULL) {
        return;
    }
    if (!ensure_lock() || xSemaphoreTake(s_destinations_lock, portMAX_DELAY) != pdTRUE) {
        oal_destinations_reset(out);
        return;
    }
    *out = s_destinations;
    xSemaphoreGive(s_destinations_lock);
}

/*
 * A refused send is not a lost packet — it is a packet that never left,
 * and it would be counted at the consumer as network loss. The Wi-Fi
 * driver's transmit buffers are a small pool, and at 200 packets a second
 * a momentary shortage is normal rather than exceptional: the first
 * measured link that lost nothing over the air still showed fifteen
 * packets missing, every one of them refused here.
 *
 * Yielding once costs microseconds against a 5 ms budget and lets the
 * driver drain, which is all a transient shortage needs.
 */
static bool send_one(int sock, const uint8_t *packet, size_t length,
                     const struct sockaddr_in *destination)
{
    for (int attempt = 0; attempt < 2; attempt++) {
        if (sendto(sock, packet, length, 0,
                   (const struct sockaddr *)destination, sizeof(*destination)) >= 0) {
            return true;
        }

        s_state.last_send_errno = errno;
        /* Only a shortage is worth retrying. A refusal for any other
         * reason will refuse again, and spinning on it would cost the
         * pacing more than the packet is worth. */
        if (errno != ENOMEM && errno != ENOBUFS) {
            break;
        }
        if (attempt == 0) {
            s_state.send_retries++;
            taskYIELD();
        }
    }

    s_state.send_errors++;
    return false;
}

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
    size_t destination_count = 0;
    /* One short of the current generation, so the first pass always loads. */
    uint32_t seen_generation = s_destinations_generation - 1u;

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

    ESP_LOGI(TAG, "streaming on port %u, ssrc %08x", s_request.port, (unsigned)header.ssrc);

    while (!s_stop) {
        if (seen_generation != s_destinations_generation) {
            seen_generation = reload_destinations(s_request.port, destinations, &destination_count);
            ESP_LOGI(TAG, "now sending to %u destination(s)", (unsigned)destination_count);
        }

        oal_rtp_header_write(&header, packet, sizeof(packet));

        if (s_request.source == OAL_RTP_SOURCE_CAPTURE) {
            oal_stream_source_t source = s_source;
            if (source != NULL) {
                source(packet + OAL_RTP_HEADER_BYTES, OAL_RTP_FRAMES_PER_PACKET);
            } else {
                /* Configured for capture with nothing attached. Silence is
                 * the honest output: the stream stays timed and a receiver
                 * keeps its buffer primed, so attaching a source later
                 * starts the audio rather than restarting the stream. */
                memset(packet + OAL_RTP_HEADER_BYTES, 0, OAL_RTP_PAYLOAD_BYTES);
            }
        } else {
            oal_rtp_fill_payload(packet + OAL_RTP_HEADER_BYTES, s_request.source,
                                 header.timestamp, s_request.tone_hz);
        }

        /* One packet, replicated byte-identically. Every consumer must see
         * the same sequence numbers and timestamps, or their measurements
         * are of different streams and cannot be compared. */
        for (size_t i = 0; i < destination_count; i++) {
            if (send_one(sock, packet, sizeof(packet), &destinations[i])) {
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
            /* Sleep whole ticks and spin the remainder. One tick is held
             * back rather than slept, because vTaskDelay guarantees only a
             * lower bound: sleeping the last one could overshoot the
             * deadline, and a send that leaves late is jitter at every
             * consumer.
             *
             * At the IDF default tick of 10 ms this whole branch was dead —
             * sleep_us never reached one tick, so the task busy-spun the
             * entire gap between packets and never blocked at all. Holding
             * a tick back only means anything once the tick is shorter than
             * a packet; see CONFIG_FREERTOS_HZ. */
            int64_t ticks = sleep_us / (1000000 / configTICK_RATE_HZ) - 1;
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
    if (request == NULL || request->destinations.count == 0) {
        return ESP_ERR_INVALID_ARG;
    }
    if (!ensure_lock()) {
        return ESP_ERR_NO_MEM;
    }

    oal_stream_producer_stop();

    s_request = *request;
    if (s_request.port == 0) {
        s_request.port = OAL_RTP_DEFAULT_PORT;
    }
    if (s_request.tone_hz == 0) {
        s_request.tone_hz = 1000;
    }

    /* Starting replaces the set outright: the request says who to play to,
     * and anything left over from a previous stream is not part of it. */
    if (xSemaphoreTake(s_destinations_lock, portMAX_DELAY) == pdTRUE) {
        s_destinations = request->destinations;
        s_destinations_generation++;
        xSemaphoreGive(s_destinations_lock);
    }

    memset(&s_state, 0, sizeof(s_state));
    s_state.running = true;
    s_state.destination_count = s_request.destinations.count;
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

void oal_stream_producer_set_source(oal_stream_source_t source)
{
    s_source = source;
}

void oal_stream_producer_get(oal_stream_producer_state_t *out)
{
    if (out != NULL) {
        *out = s_state;
    }
}
