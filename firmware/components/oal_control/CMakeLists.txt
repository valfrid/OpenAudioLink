idf_component_register(
    SRCS "oal_control.c"
         "oal_join.c"
    INCLUDE_DIRS "include"
    REQUIRES oal_config oal_rtp oal_discovery
    PRIV_REQUIRES esp_http_client esp_http_server esp_https_ota esp_partition esp_wifi esp_timer json)
