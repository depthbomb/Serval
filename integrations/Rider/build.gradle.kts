import org.jetbrains.intellij.platform.gradle.tasks.PrepareSandboxTask
import org.jetbrains.intellij.platform.gradle.TestFrameworkType

plugins {
    java
    id("org.jetbrains.intellij.platform") version "2.20.1"
}

group = "dev.serval"
version = providers.gradleProperty("pluginVersion").get()

repositories {
    mavenCentral()
    intellijPlatform { defaultRepositories() }
}

dependencies {
    intellijPlatform {
        testFramework(TestFrameworkType.Platform)
        val riderPath = providers.gradleProperty("riderPath").orElse(providers.environmentVariable("RIDER_HOME"))
        if (riderPath.isPresent) {
            local(riderPath.get())
        } else {
            rider(providers.gradleProperty("platformVersion"))
        }
    }
    testImplementation("junit:junit:4.13.2")
}

java {
    sourceCompatibility = JavaVersion.VERSION_25
    targetCompatibility = JavaVersion.VERSION_25
}

tasks.test {
    providers.gradleProperty("riderDotnetPath").orNull?.let {
        // Rider's SDK bootstrap otherwise inherits the repository's global.json.
        systemProperty("rider.backend.dotnet.runtime.path", it)
    }
}

intellijPlatform {
    pluginConfiguration {
        ideaVersion {
            sinceBuild = "263"
            untilBuild = "263.*"
        }
    }
    instrumentCode = false
    // Rider loads its backend to index settings. Keep the whole indexing pipeline optional.
    buildSearchableOptions = providers.gradleProperty("indexSettings").map(String::toBoolean).orElse(false)
}

val serverProject = layout.projectDirectory.dir("../../src/Serval.Savanna.LanguageServer")
val serverOutput = layout.buildDirectory.dir("language-server")
val publishLanguageServer = tasks.register<Exec>("publishLanguageServer") {
    workingDir = layout.projectDirectory.dir("../..").asFile
    commandLine("dotnet", "publish", serverProject.file("Serval.Savanna.LanguageServer.csproj").asFile,
        "--configuration", "Release", "--output", serverOutput.get().asFile,
        "--no-self-contained", "--disable-build-servers", "-p:UseAppHost=false")
    inputs.files(fileTree(serverProject) { exclude("bin/**", "obj/**") })
    inputs.files(fileTree(layout.projectDirectory.dir("../../src/Serval.Savanna.Compiler")) { exclude("bin/**", "obj/**") })
    inputs.files(layout.projectDirectory.file("../../Directory.Build.props"), layout.projectDirectory.file("../../global.json"))
    outputs.dir(serverOutput)
}

tasks.withType<PrepareSandboxTask>().configureEach {
    dependsOn(publishLanguageServer)
    from(serverOutput) { into("${rootProject.name}/language-server") }
}

