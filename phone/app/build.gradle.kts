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
        versionCode = 34
        versionName = "0.10.0"
    }

    /*
     * Release signing, from outside the tree and never from inside it.
     *
     * A release keystore is the same class of secret as the Wi-Fi
     * credentials this project keeps out of the repository: whoever holds
     * it can publish an update that a phone installs over this one without
     * asking. So it arrives as a path and three passwords, from Gradle
     * properties or the environment, and nothing here has a default.
     *
     * **The key must never change.** Android refuses to install an update
     * signed by a different key than the installed app, with no way round
     * it but uninstalling — which takes the Spotify sign-in and the saved
     * stations with it. One key, kept somewhere it cannot be lost, for the
     * life of the application id.
     *
     * The debug build is untouched: it still signs with the throwaway
     * debug key, which is why CI can build an APK on every push without
     * any of this being configured.
     */
    val keystore = providers.gradleProperty("oal.keystore")
        .orElse(providers.environmentVariable("OAL_KEYSTORE"))
    val keystorePassword = providers.gradleProperty("oal.keystore.password")
        .orElse(providers.environmentVariable("OAL_KEYSTORE_PASSWORD"))
    val keyAlias = providers.gradleProperty("oal.key.alias")
        .orElse(providers.environmentVariable("OAL_KEY_ALIAS"))
    val keyPassword = providers.gradleProperty("oal.key.password")
        .orElse(providers.environmentVariable("OAL_KEY_PASSWORD"))
    val signable = keystore.isPresent && keystorePassword.isPresent
        && keyAlias.isPresent && keyPassword.isPresent

    if (signable) {
        signingConfigs.create("release") {
            storeFile = file(keystore.get())
            storePassword = keystorePassword.get()
            this.keyAlias = keyAlias.get()
            this.keyPassword = keyPassword.get()
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            /*
             * Null rather than the debug key when nothing is configured.
             *
             * Falling back to debug is what this used to do, and it is the
             * worst of the options: it produces an APK that installs
             * perfectly, updates nothing that came before it, and cannot
             * be updated by anything after it — a mistake that is only
             * visible months later. Unsigned plus the loud check below
             * fails while somebody is still looking.
             */
            signingConfig = if (signable) signingConfigs.getByName("release") else null
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

/*
 * The packaging revision, which this used to ignore.
 *
 * `librespot-android.yml` publishes to `librespot-android-v<version>-<rev>`
 * and this fetched `librespot-android-v<version>` with no revision at all,
 * so every rebuild landed on a tag the app never looked at. It worked only
 * because an early revision-less release happened to exist.
 *
 * Empty means the old tag, which is what is published today. Set it to the
 * revision once a new one exists:
 *
 *     ./gradlew :app:assembleDebug -Poal.librespot.revision=3
 *
 * or change the default here. The first build that needs the 16 KB aligned
 * binary is the one that needs this set.
 */
val librespotRevision = providers.gradleProperty("oal.librespot.revision").orElse("")

val fetchLibrespot by tasks.registering {
    description = "Downloads the published librespot build for arm64 into the app."
    group = "build setup"

    val version = librespotVersion.get()
    val repo = librespotRepo.get()
    val revision = librespotRevision.get().takeIf { it.isNotBlank() }?.let { "-$it" }.orEmpty()
    val target = layout.projectDirectory
        .file("src/main/jniLibs/arm64-v8a/liblibrespot.so").asFile
    outputs.file(target)

    doLast {
        val base =
            "https://github.com/$repo/releases/download/librespot-android-v$version$revision"
        target.parentFile.mkdirs()

        val expected = try {
            URI("$base/liblibrespot.so.sha256").toURL()
                .readText().trim().substringBefore(' ')
        } catch (e: Exception) {
            throw GradleException(
                "librespot-android-v$version$revision publishes no SHA256, or could not be " +
                    "reached ($e). Refusing to install it. Run the librespot-android workflow " +
                    "first, and check -Poal.librespot.revision matches the tag it published."
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

/*
 * A release build with no key stops here rather than shipping.
 *
 * The APK would otherwise be produced unsigned, and an unsigned APK does
 * not install — which is a confusing way to learn that four secrets were
 * missing, and only after the build claimed success.
 */
tasks.matching { it.name == "assembleRelease" || it.name == "bundleRelease" }
    .configureEach {
        doFirst {
            val keystore = providers.gradleProperty("oal.keystore")
                .orElse(providers.environmentVariable("OAL_KEYSTORE"))
            if (!keystore.isPresent) {
                throw GradleException(
                    "A release build needs a signing key, and none is configured.\n" +
                        "  Set oal.keystore, oal.keystore.password, oal.key.alias and\n" +
                        "  oal.key.password as Gradle properties, or the same names as\n" +
                        "  OAL_KEYSTORE, OAL_KEYSTORE_PASSWORD, OAL_KEY_ALIAS and\n" +
                        "  OAL_KEY_PASSWORD in the environment.\n" +
                        "  See docs/PHONE-APP.md, 'Releases and updating'.\n" +
                        "  For a throwaway build, use assembleDebug instead."
                )
            }
        }
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
