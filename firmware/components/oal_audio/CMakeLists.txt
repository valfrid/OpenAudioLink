idf_component_register(
    SRCS "oal_eq.c"
         "oal_fade.c"
         "oal_input.c"
         "oal_output.c"
         "oal_pcm.c"
         "oal_playout.c"
         "oal_sink.c"
         "oal_sink_i2s.c"
         "oal_sink_usb.c"
         "oal_capture.c"
    INCLUDE_DIRS "include"
    REQUIRES oal_rtp
    # usb and uac2_host for the dongle output stage (docs/USB-AUDIO.md).
    # Linked into every node rather than selected by a build flag, so one
    # image serves a board with a DAC and a board with a dongle alike.
    PRIV_REQUIRES driver esp_timer usb uac2_host)
