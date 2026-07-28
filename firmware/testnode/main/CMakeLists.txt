idf_component_register(
    SRCS "main.c"
    PRIV_REQUIRES nvs_flash esp_timer esp_wifi esp_netif oal_wifi oal_discovery oal_control)
