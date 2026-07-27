idf_component_register(
    SRCS "oal_discovery.c"
    INCLUDE_DIRS "include"
    REQUIRES lwip
    PRIV_REQUIRES json esp_hw_support)
