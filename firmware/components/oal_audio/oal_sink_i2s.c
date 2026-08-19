/*
 * The I²S sink: the reference receiver's output stage (docs/HARDWARE.md).
 *
 * Moved out of `oal_playout.c` unchanged when the USB dongle became a
 * second option. Nothing here is new — the configuration, the comments and
 * the reasons are the ones that were already working — and keeping it that
 * way is the point: a refactor that quietly improves the thing it moves is
 * a refactor nobody can bisect.
 */

#include "oal_sink.h"

#include "driver/i2s_std.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "oal_rtp.h"

static const char *TAG = "oal_sink_i2s";

/* 5 ms at 48 kHz, matching the RTP packet, and four descriptors of it —
 * the depth that stopped the first hardware dropouts. */
#define CHUNK_FRAMES     240
#define DMA_DESCRIPTORS  4

static i2s_chan_handle_t s_tx;

static esp_err_t i2s_open(const oal_sink_config_t *config)
{
    i2s_chan_config_t channel_config =
        I2S_CHANNEL_DEFAULT_CONFIG(I2S_NUM_AUTO, I2S_ROLE_MASTER);
    channel_config.dma_desc_num = DMA_DESCRIPTORS;
    channel_config.dma_frame_num = CHUNK_FRAMES;
    /* Underrun should be silence, not the last buffer played again — a
     * repeated 5 ms of audio is a buzz, and a recognisable one. */
    channel_config.auto_clear = true;

    esp_err_t err = i2s_new_channel(&channel_config, &s_tx, NULL);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "i2s_new_channel failed: %s", esp_err_to_name(err));
        return err;
    }

    i2s_std_config_t std_config = {
        .clk_cfg = I2S_STD_CLK_DEFAULT_CONFIG(config->sample_rate),
        /* 32-bit slots carrying 24-bit samples left-justified. The PCM5102A
         * and the MAX98357A both take the leading 24 bits and ignore the
         * padding, so one configuration drives either board. */
        .slot_cfg = I2S_STD_PHILIPS_SLOT_DEFAULT_CONFIG(
            I2S_DATA_BIT_WIDTH_32BIT, I2S_SLOT_MODE_STEREO),
        .gpio_cfg = {
            /* No master clock. The PCM5102A runs its internal PLL from the
             * bit clock when its SCK pin is grounded, and the MAX98357A
             * never wanted one — which is why both are wired with three
             * signals instead of four (docs/HARDWARE.md). */
            .mclk = I2S_GPIO_UNUSED,
            .bclk = config->bclk_gpio,
            .ws   = config->ws_gpio,
            .dout = config->dout_gpio,
            .din  = I2S_GPIO_UNUSED,
            .invert_flags = {
                .mclk_inv = false,
                .bclk_inv = false,
                .ws_inv   = false,
            },
        },
    };

    err = i2s_channel_init_std_mode(s_tx, &std_config);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "i2s_channel_init_std_mode failed: %s", esp_err_to_name(err));
        i2s_del_channel(s_tx);
        s_tx = NULL;
        return err;
    }

    err = i2s_channel_enable(s_tx);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "i2s_channel_enable failed: %s", esp_err_to_name(err));
        i2s_del_channel(s_tx);
        s_tx = NULL;
        return err;
    }

    ESP_LOGI(TAG, "I2S out on BCLK=%d WS=%d DOUT=%d, %" PRIu32 " Hz",
             config->bclk_gpio, config->ws_gpio, config->dout_gpio,
             config->sample_rate);
    return ESP_OK;
}

static void i2s_close(void)
{
    if (s_tx == NULL) {
        return;
    }
    i2s_channel_disable(s_tx);
    i2s_del_channel(s_tx);
    s_tx = NULL;
}

/*
 * Open is ready. The pins exist whether or not a DAC is soldered to them,
 * and nothing in this design can tell the difference — which is the honest
 * answer rather than a limitation to apologise for.
 */
static bool i2s_ready(void)
{
    return s_tx != NULL;
}

static esp_err_t i2s_write(const void *data, size_t bytes, size_t *written,
                           uint32_t timeout_ms)
{
    return i2s_channel_write(s_tx, data, bytes, written, pdMS_TO_TICKS(timeout_ms));
}

const oal_sink_t *oal_sink_i2s(void)
{
    static const oal_sink_t sink = {
        .name = "I2S",
        .open = i2s_open,
        .close = i2s_close,
        .ready = i2s_ready,
        .write = i2s_write,
    };
    return &sink;
}
