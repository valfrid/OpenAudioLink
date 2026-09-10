import java.net.URI
import java.security.MessageDigest

/*
 * Imported, not written out.
 *
 * In a Gradle Kotlin DSL script `java` resolves to the Java plugin's
 * extension, not to the package root, so `java.net.URI` is read as
 * `javaExtension.net.URI` and fails with "Unresolved reference: net" —
 * pointing at the wrong thing entirely.
 */

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
        /*
         * The identity Google Play will hold forever.
         *
         * An applicationId cannot be changed once a package has been
         * published: Play keys the listing, the reviews, the install base
         * and the update path on it, and a new one is a different app with
         * a different listing that no existing installation upgrades to.
         * So it is worth being deliberate about now and never again.
         *
         * It differs from `namespace` above on purpose. The namespace is
         * where the generated `R` and `BuildConfig` classes live and it
         * follows the Kotlin source tree; the applicationId is the name
         * the device and the store use. Keeping them apart means the
         * store identity could be settled without moving every file.
         */
        applicationId = "se.valfrid.openaudiolink"
        minSdk = 26          // AudioTrack float output, notification channels
        targetSdk = 35
        versionCode = 33
        versionName = "0.9.1"
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

    packaging {
        jniLibs {
            /*
             * Extract the native libraries to a real directory.
             *
             * The modern default keeps them compressed inside the APK and
             * maps them from there, which is fine for something you
             * dlopen() and useless for something you exec(): there is no
             * path to hand ProcessBuilder. librespot is an executable, so
             * it has to be a file on disk — and the native library
             * directory is the only place Android will run one from.
             */
            useLegacyPackaging = true
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions { jvmTarget = "17" }

    /*
     * `buildConfig` is on so the app can read its own versionName.
     *
     * It was carried a second time as a hand-written constant, and the
     * two drifted the moment anybody forgot one of them — which is a
     * thing that happened repeatedly. One number, declared here, read
     * everywhere.
     */
    buildFeatures {
        compose = true
        buildConfig = true
    }
}

/*
 * Fetching librespot, the same way the Hub does.
 *
 * `hub/scripts/get-librespot.ps1` downloads a published build and checks it
 * against the SHA256 that release publishes, refusing to install one that
 * publishes no hash. This is that, in Gradle, for the same reason: the
 * binary is on a different clock from this app — it changes when librespot
 * changes — so it is a published artefact fetched on demand rather than
 * something rebuilt on every push.
 *
 * Run `.github/workflows/librespot-android.yml` once to produce the
 * release this reads.
 */
val librespotVersion = providers.gradleProperty("oal.librespot.version").orElse("0.8.0")
val librespotRepo = providers.gradleProperty("oal.librespot.repo")
    .orElse("valfrid/OpenAudioLink")

val fetchLibrespot by tasks.registering {
    description = "Downloads the published librespot build for arm64 into the app."
    group = "build setup"

    val version = librespotVersion.get()
    val repo = librespotRepo.get()
    val target = layout.projectDirectory
        .file("src/main/jniLibs/arm64-v8a/liblibrespot.so").asFile
    outputs.file(target)

    doLast {
        val base = "https://github.com/$repo/releases/download/librespot-android-v$version"
        target.parentFile.mkdirs()

        val expected = try {
            URI("$base/liblibrespot.so.sha256").toURL()
                .readText().trim().substringBefore(' ')
        } catch (e: Exception) {
            throw GradleException(
                "librespot-android-v$version publishes no SHA256, or could not be reached " +
                    "($e). Refusing to install it. Run the librespot-android workflow first."
            )
        }

        val bytes = URI("$base/liblibrespot.so").toURL().readBytes()
        val actual = MessageDigest.getInstance("SHA-256")
            .digest(bytes)
            .joinToString("") { "%02x".format(it) }

        if (!actual.equals(expected, ignoreCase = true)) {
            throw GradleException(
                "librespot download does not match its published SHA256.\n" +
                    "  expected $expected\n  got      $actual"
            )
        }

        target.writeBytes(bytes)
        logger.lifecycle("librespot $version: ${bytes.size} bytes, SHA256 verified")
    }
}

/*
 * Every build waits for it, because there is only one build and Spotify is
 * what the phone hub is for. A missing librespot release therefore fails
 * the build with the message above rather than quietly producing an app
 * whose main feature does nothing.
 */
tasks.matching { it.name.startsWith("merge") && it.name.contains("JniLibFolders") }
    .configureEach { dependsOn(fetchLibrespot) }

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
