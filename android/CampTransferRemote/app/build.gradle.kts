plugins {
    id("com.android.application")
}

android {
    namespace = "com.mixtervee.camptransferremote"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.mixtervee.camptransferremote"
        minSdk = 26
        targetSdk = 32
        versionCode = 5
        versionName = "0.2.1"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}
