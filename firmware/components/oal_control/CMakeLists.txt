idf_component_register(
    SRCS "oal_control.c"
         "oal_join.c"
    INCLUDE_DIRS "include"
    REQUIRES oal_config oal_rtp oal_discovery oal_audio oal_wifi
    # app_update for esp_ota_ops.h, which /status uses to report which slot
    # is running and whether it has confirmed itself. Not implied by
    # esp_https_ota: that one installs an image, this one asks after it.
    PRIV_REQUIRES app_update esp_http_client esp_http_server esp_https_ota
                  esp_partition esp_wifi esp_timer json)
