/*
 * The Android half. Built only where an SDK exists — see settings.gradle.kts,
 * which is also where these plugins' versions are pinned.
 */
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "org.openaudiolink.phone"
    compileSdk = 35

    defaultConfig {
        applicationId = "org.openaudiolink.phone"
        minSdk = 26          // AudioTrack float output, notification channels
        targetSdk = 35
        versionCode = 2
        versionName = "0.2.0"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            /*
             * Signed with the debug key unless a keystore is supplied.
             *
             * A release keystore is never committed. It is the same class
             * of secret as the Wi-Fi credentials this project already
             * keeps out of the tree: whoever holds it can publish an
             * update that a phone installs over this one without warning.
             * Supply one through ~/.gradle/gradle.properties if you want
             * reproducible updates on your own device.
             */
            signingConfig = signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions { jvmTarget = "17" }

    buildFeatures { compose = true }
}

dependencies {
    implementation(project(":core"))

    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.activity:activity-compose:1.9.3")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.7")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")

    val compose = platform("androidx.compose:compose-bom:2024.10.01")
    implementation(compose)
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")

    // The decoder, and with it the resampler. Media3 is also what an
    // internet-radio source would be built on, which is why the source
    // interface is shaped the way it is.
    implementation("androidx.media3:media3-exoplayer:1.4.1")
    implementation("androidx.media3:media3-common:1.4.1")
}
