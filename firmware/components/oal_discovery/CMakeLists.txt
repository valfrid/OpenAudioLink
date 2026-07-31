idf_component_register(
    SRCS "oal_discovery.c"
         "oal_peers.c"
    INCLUDE_DIRS "include"
    REQUIRES lwip oal_config
    PRIV_REQUIRES json esp_hw_support esp_timer)
