idf_component_register(
    SRCS "main.c"
    # app_update for esp_ota_ops.h: confirming a freshly installed image so
    # the bootloader stops holding it on probation. esp_app_format is the
    # neighbouring component and covers only esp_app_desc.h, the version
    # string -- reading a description is not the same as marking a slot
    # valid, and only one of them keeps the node off its previous firmware.
    PRIV_REQUIRES nvs_flash esp_timer esp_wifi esp_netif esp_app_format app_update
                  oal_config oal_wifi oal_discovery oal_control oal_rtp oal_audio)
