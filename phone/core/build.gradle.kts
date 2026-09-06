import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    kotlin("jvm")
    kotlin("plugin.serialization")
}

dependencies {
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.7.3")
    testImplementation(kotlin("test"))
}

/*
 * Bytecode 17, which is what current Android accepts, while building on
 * whatever JDK the machine has. A toolchain pin would be stricter and
 * would also mean this module cannot be built on a CI image that ships a
 * newer JDK and no downloader — which is most of them.
 */
java {
    sourceCompatibility = JavaVersion.VERSION_17
    targetCompatibility = JavaVersion.VERSION_17
}

kotlin {
    compilerOptions { jvmTarget = JvmTarget.JVM_17 }
}

tasks.test { useJUnitPlatform() }
