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

    /*
     * Every plugin version, in one place and applied nowhere.
     *
     * Not in the root build file. A `plugins { … apply false }` block there
     * puts the plugin on the classpath of every project, and the Kotlin JVM
     * and Kotlin Android plugins ship in the same artefact — so `:app`
     * asking for `org.jetbrains.kotlin.android` at a version was refused
     * with "already on the classpath with an unknown version". Declaring
     * versions here sets a default for each id and resolves nothing until a
     * project actually applies it, which is what lets `:core` build on a
     * machine that cannot reach Google's Maven at all.
     */
    plugins {
        id("org.jetbrains.kotlin.jvm") version "2.0.21"
        id("org.jetbrains.kotlin.plugin.serialization") version "2.0.21"
        id("com.android.application") version "8.7.2"
        id("org.jetbrains.kotlin.android") version "2.0.21"
        id("org.jetbrains.kotlin.plugin.compose") version "2.0.21"
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
 * The Android module joins the build only where an SDK exists, so that
 * `gradle :core:test` on a machine with a JDK and nothing else runs the
 * tests it can rather than failing on the Android plugin.
 *
 * `OAL_CORE_ONLY` forces it out even where an SDK is present. CI's hosted
 * runners set ANDROID_HOME for every job, so without this the core job
 * would quietly configure the Android module — and the claim that these
 * tests need no SDK would stop being checked by anything.
 */
val coreOnly = System.getenv("OAL_CORE_ONLY") != null
val haveSdk = System.getenv("ANDROID_HOME") != null ||
    System.getenv("ANDROID_SDK_ROOT") != null ||
    file("local.properties").exists()

if (!coreOnly && haveSdk) {
    include(":app")
}
