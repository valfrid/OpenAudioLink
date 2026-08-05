#include "oal_join.h"

#include <string.h>

#include "esp_http_client.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "oal_discovery.h"

static const char *TAG = "oal_join";

static char s_id[OAL_PEER_ID_MAX];
static uint16_t s_rtp_port;
static volatile bool s_acknowledged;
static char s_last_status[16];

bool oal_join_acknowledged(void)
{
    return s_acknowledged;
}

const char *oal_join_last_status(void)
{
    return s_last_status;
}

/*
 * The Controller's answer is short and its only field that matters is the
 * status, so it is scanned rather than parsed. Pulling in a JSON parser to
 * read one word would cost more heap than the whole exchange.
 */
static void remember_status(const char *body)
{
    const char *found = strstr(body, "\"status\"");
    if (found == NULL) {
        return;
    }
    const char *open = strchr(found + 8, '"');
    if (open == NULL) {
        return;
    }
    const char *close = strchr(open + 1, '"');
    if (close == NULL || (size_t)(close - open - 1) >= sizeof(s_last_status)) {
        return;
    }
    memcpy(s_last_status, open + 1, (size_t)(close - open - 1));
    s_last_status[close - open - 1] = '\0';
}

static bool ask(const oal_peer_t *controller)
{
    char url[64];
    if (snprintf(url, sizeof(url), "http://%s:%u/join",
                 controller->address, (unsigned)controller->control_port)
            >= (int)sizeof(url)) {
        return false;
    }

    char body[96];
    int length = snprintf(body, sizeof(body), "{\"id\":\"%s\",\"port\":%u}",
                          s_id, (unsigned)s_rtp_port);
    if (length <= 0 || length >= (int)sizeof(body)) {
        return false;
    }

    esp_http_client_config_t config = {
        .url = url,
        .method = HTTP_METHOD_POST,
        /* Short: a Controller that does not answer promptly is one we will
         * ask again shortly anyway, and a long block here would hold the
         * task through a Controller's reboot. */
        .timeout_ms = 3000,
    };
    esp_http_client_handle_t client = esp_http_client_init(&config);
    if (client == NULL) {
        return false;
    }

    esp_http_client_set_header(client, "Content-Type", "application/json");

    /*
     * Opened and read rather than performed. esp_http_client_perform()
     * consumes the response itself, so reading afterwards yields nothing —
     * the request succeeded and the Controller's answer was thrown away,
     * which showed up as a node that had plainly joined and could not say
     * what it had been told.
     */
    bool accepted = false;
    if (esp_http_client_open(client, length) == ESP_OK) {
        if (esp_http_client_write(client, body, length) == length
                && esp_http_client_fetch_headers(client) >= 0) {
            char answer[96] = { 0 };
            int read = esp_http_client_read(client, answer, sizeof(answer) - 1);
            if (read > 0) {
                answer[read] = '\0';
                remember_status(answer);
            }

            /*
             * Any answer at all counts. A Controller that refuses, or one
             * too old to know what joining is, has still told us it exists
             * — and standing by is the right thing to do in both cases.
             * Treating a refusal as a failure would mean asking every five
             * seconds forever.
             */
            int status = esp_http_client_get_status_code(client);
            accepted = status >= 200 && status < 500;
            if (!accepted) {
                ESP_LOGW(TAG, "join to %s answered %d", controller->name, status);
            }
        }
        esp_http_client_close(client);
    } else {
        /*
         * Worth a line. A Controller that answers badly already logs, but
         * one that does not answer at all said nothing — and that is the
         * case that repeats every five seconds forever, which is exactly
         * what a stale peer looks like after a Hub moves to another
         * machine. Silence here made a node retrying a dead address
         * indistinguishable from a node with nothing to do.
         */
        ESP_LOGW(TAG, "join to %s at %s did not connect",
                 controller->name, controller->address);
    }

    esp_http_client_cleanup(client);
    return accepted;
}

static void join_task(void *arg)
{
    (void)arg;

    for (;;) {
        oal_peer_t controller;
        oal_controller_who_t who = oal_discovery_controller(&controller);

        if (who == OAL_CONTROLLER_PEER && ask(&controller)) {
            if (!s_acknowledged) {
                ESP_LOGI(TAG, "joined %s: %s", controller.name,
                         s_last_status[0] ? s_last_status : "accepted");
            }
            s_acknowledged = true;
            vTaskDelay(pdMS_TO_TICKS(OAL_JOIN_REFRESH_MS));
            continue;
        }

        /*
         * No Controller yet, or it did not answer. Asking again shortly is
         * the whole recovery mechanism: announcements are periodic, a
         * Producer claims the role within seconds of finding nobody, and a
         * Consumer that keeps asking will be there when it does.
         */
        s_acknowledged = false;
        vTaskDelay(pdMS_TO_TICKS(OAL_JOIN_RETRY_MS));
    }
}

esp_err_t oal_join_start(const char *id, uint16_t rtp_port)
{
    if (id == NULL || id[0] == '\0') {
        return ESP_ERR_INVALID_ARG;
    }

    snprintf(s_id, sizeof(s_id), "%s", id);
    s_rtp_port = rtp_port;
    s_last_status[0] = '\0';

    /* Below the audio tasks and the control server: joining is how a
     * speaker starts, not something that may interrupt one already
     * playing. */
    if (xTaskCreate(join_task, "oal_join", 4096, NULL, 4, NULL) != pdPASS) {
        return ESP_ERR_NO_MEM;
    }
    return ESP_OK;
}
