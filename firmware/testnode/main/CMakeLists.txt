idf_component_register(
    SRCS "main.c"
    PRIV_REQUIRES nvs_flash esp_timer esp_wifi esp_netif esp_app_format
                  oal_config oal_wifi oal_discovery oal_control oal_rtp oal_audio)
