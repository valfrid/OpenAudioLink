/*
 * Deliberately empty of plugin declarations.
 *
 * Versions live in `settings.gradle.kts` under `pluginManagement`, because
 * a `plugins { … apply false }` block here would put the Kotlin plugin on
 * every project's classpath — and the JVM and Android Kotlin plugins are
 * the same artefact, so `:app` could then no longer ask for its own
 * version. It would also resolve the Android Gradle Plugin from Google's
 * Maven on machines that only ever build `:core`.
 */
