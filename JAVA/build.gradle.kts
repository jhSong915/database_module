plugins {
    `java-library`
}

repositories {
    mavenCentral()
}

java {
    toolchain {
        languageVersion.set(JavaLanguageVersion.of(21)) // JDK 21 지정
    }
}

dependencies {
    // MSSQL 공식 JDBC 드라이버 (Java 21 호환)
    implementation("com.microsoft.sqlserver:mssql-jdbc:12.8.1.jre11")

    // 테스트
    testImplementation(platform("org.junit:junit-bom:5.10.2"))
    testImplementation("org.junit.jupiter:junit-jupiter")
}

tasks.test {
    useJUnitPlatform()
}
