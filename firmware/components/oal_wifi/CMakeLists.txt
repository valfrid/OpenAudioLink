idf_component_register(
    SRCS "oal_wifi.c"
    INCLUDE_DIRS "include"
    REQUIRES esp_wifi esp_netif oal_config
    PRIV_REQUIRES esp_http_server esp_event nvs_flash)
