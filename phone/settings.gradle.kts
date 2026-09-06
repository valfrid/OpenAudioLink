/*
 * Two modules, and the split is deliberate.
 *
 * `core` is plain Kotlin on the JVM: the wire format, the pacing, the
 * discovery protocol and the control requests. It has no Android in it, so
 * it builds and its tests run on any machine with a JDK — including CI,
 * which needs no Android SDK to check the part that has to be exactly
 * right. This is the same discipline the firmware uses for `oal_phase` and
 * `oal_netpick`: arithmetic that cannot be debugged by looking at the
 * device is tested where it can be.
 *
 * `app` is the Android half — the UI, the decoder, the foreground service
 * and the radio locks. It needs the Android SDK.
 */
pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "openaudiolink-phone"

include(":core")

/*
 * The Android module joins the build only where an SDK exists. Without
 * this, `gradle :core:test` on a machine with a JDK and nothing else fails
 * on the Android plugin rather than running the tests it could — which
 * would make the whole point of splitting the modules moot.
 */
if (System.getenv("ANDROID_HOME") != null ||
    System.getenv("ANDROID_SDK_ROOT") != null ||
    file("local.properties").exists()
) {
    include(":app")
}
