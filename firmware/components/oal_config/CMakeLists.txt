idf_component_register(
    SRCS "oal_config.c"
    INCLUDE_DIRS "include"
    # oal_audio for OAL_VOLUME_DEFAULT: the stored volume and the gain curve
    # that gives it meaning must agree about what "unset" is.
    REQUIRES oal_rtp oal_audio
    PRIV_REQUIRES nvs_flash)
