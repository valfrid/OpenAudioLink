idf_component_register(
    SRCS "oal_pcm.c"
         "oal_playout.c"
    INCLUDE_DIRS "include"
    REQUIRES oal_rtp
    PRIV_REQUIRES driver)
