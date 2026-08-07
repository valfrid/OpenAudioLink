idf_component_register(
    SRCS "oal_wifi.c"
    INCLUDE_DIRS "include"
    REQUIRES esp_wifi esp_netif oal_config
    # esp_timer for the reconnect backoff: the delay cannot be taken in the
    # event handler, which runs on the event task and would stall every
    # other event in the system including the one saying the join worked.
    PRIV_REQUIRES esp_http_server esp_event nvs_flash esp_timer)
