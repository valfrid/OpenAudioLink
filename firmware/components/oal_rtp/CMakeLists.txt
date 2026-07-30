idf_component_register(
    SRCS "oal_rtp.c"
         "oal_rtp_stats.c"
         "oal_stream_producer.c"
         "oal_stream_consumer.c"
    INCLUDE_DIRS "include"
    REQUIRES lwip
    PRIV_REQUIRES esp_timer esp_hw_support)
