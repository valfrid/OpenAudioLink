idf_component_register(
    SRCS "oal_config.c"
    INCLUDE_DIRS "include"
    REQUIRES oal_rtp
    PRIV_REQUIRES nvs_flash)
