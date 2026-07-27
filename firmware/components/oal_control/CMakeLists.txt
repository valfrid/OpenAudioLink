idf_component_register(
    SRCS "oal_control.c"
    INCLUDE_DIRS "include"
    PRIV_REQUIRES esp_http_server esp_https_ota esp_wifi esp_timer json)
