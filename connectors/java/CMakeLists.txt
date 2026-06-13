cmake_minimum_required(VERSION 3.21)

project(SonnetDBJava
    VERSION 0.1.0
    DESCRIPTION "SonnetDB Java connector"
    LANGUAGES C)

set(SONNETDB_JAVA_BASE_RELEASE "8" CACHE STRING "Java release used by the public API and JNI backend.")
set(SONNETDB_JAVA_FFM_RELEASE "21" CACHE STRING "Java release used by the FFM backend.")
option(SONNETDB_JAVA_BUILD_FFM "Build the optional Java 21+ FFM backend into a multi-release jar." ON)
option(SONNETDB_JAVA_ENABLE_PREVIEW "Compile and run FFM with --enable-preview for JDK 21." ON)
option(SONNETDB_JAVA_BUILD_EXAMPLES "Build Java connector examples." ON)
set(SONNETDB_JAVA_NATIVE_LIBRARY "" CACHE FILEPATH "Path to SonnetDB.Native.dll or SonnetDB.Native.so for the run target.")

find_package(Java REQUIRED COMPONENTS Development)
find_package(JNI REQUIRED COMPONENTS JVM)

execute_process(
    COMMAND "${Java_JAVA_EXECUTABLE}" -version
    OUTPUT_VARIABLE _sonnetdb_java_version_stdout
    ERROR_VARIABLE _sonnetdb_java_version_stderr
    OUTPUT_STRIP_TRAILING_WHITESPACE
    ERROR_STRIP_TRAILING_WHITESPACE)
set(_sonnetdb_java_version_text "${_sonnetdb_java_version_stdout}${_sonnetdb_java_version_stderr}")
if(_sonnetdb_java_version_text MATCHES "version \"([0-9]+)(\\.([0-9]+))?")
    set(_sonnetdb_java_version_major "${CMAKE_MATCH_1}")
    set(_sonnetdb_java_version_minor "${CMAKE_MATCH_3}")
    if(_sonnetdb_java_version_major STREQUAL "1")
        set(SONNETDB_JAVA_FEATURE_VERSION "${_sonnetdb_java_version_minor}")
    else()
        set(SONNETDB_JAVA_FEATURE_VERSION "${_sonnetdb_java_version_major}")
    endif()
else()
    set(SONNETDB_JAVA_FEATURE_VERSION "8")
endif()
message(STATUS "SonnetDB Java connector JDK feature version: ${SONNETDB_JAVA_FEATURE_VERSION}")

if(SONNETDB_JAVA_BUILD_FFM AND SONNETDB_JAVA_FEATURE_VERSION LESS SONNETDB_JAVA_FFM_RELEASE)
    message(FATAL_ERROR
        "SONNETDB_JAVA_BUILD_FFM=ON requires JDK ${SONNETDB_JAVA_FFM_RELEASE}+; "
        "configure with -DSONNETDB_JAVA_BUILD_FFM=OFF for the Java 8-compatible JNI-only build.")
endif()

set(SONNETDB_JAVA_CLASSES_DIR "${CMAKE_CURRENT_BINARY_DIR}/classes")
set(SONNETDB_JAVA_FFM_CLASSES_DIR "${CMAKE_CURRENT_BINARY_DIR}/ffm-classes")
set(SONNETDB_JAVA_EXAMPLE_CLASSES_DIR "${CMAKE_CURRENT_BINARY_DIR}/example-classes")
set(SONNETDB_JAVA_JAR "${CMAKE_CURRENT_BINARY_DIR}/sonnetdb-java.jar")
set(SONNETDB_JAVA_MANIFEST "${CMAKE_CURRENT_BINARY_DIR}/MANIFEST.MF")

file(WRITE "${SONNETDB_JAVA_MANIFEST}" "Manifest-Version: 1.0\nMulti-Release: true\n")

file(GLOB_RECURSE SONNETDB_JAVA_SOURCE_FILES CONFIGURE_DEPENDS
    "${CMAKE_CURRENT_SOURCE_DIR}/src/main/java/*.java")
file(GLOB_RECURSE SONNETDB_JAVA_FFM_SOURCE_FILES CONFIGURE_DEPENDS
    "${CMAKE_CURRENT_SOURCE_DIR}/src/ffm/java/*.java")

if(SONNETDB_JAVA_FEATURE_VERSION LESS 9)
    set(SONNETDB_JAVAC_BASE_FLAGS -source "${SONNETDB_JAVA_BASE_RELEASE}" -target "${SONNETDB_JAVA_BASE_RELEASE}")
else()
    set(SONNETDB_JAVAC_BASE_FLAGS --release "${SONNETDB_JAVA_BASE_RELEASE}")
endif()
list(APPEND SONNETDB_JAVAC_BASE_FLAGS -Xlint:-options)

if(SONNETDB_JAVA_FEATURE_VERSION LESS 9)
    set(SONNETDB_JAVA_JAR_CREATE_COMMAND
        "${Java_JAR_EXECUTABLE}"
        cfm
        "${SONNETDB_JAVA_JAR}"
        "${SONNETDB_JAVA_MANIFEST}"
        -C "${SONNETDB_JAVA_CLASSES_DIR}" .)
else()
    set(SONNETDB_JAVA_JAR_CREATE_COMMAND
        "${Java_JAR_EXECUTABLE}"
        --create
        --file "${SONNETDB_JAVA_JAR}"
        --manifest "${SONNETDB_JAVA_MANIFEST}"
        -C "${SONNETDB_JAVA_CLASSES_DIR}" .)
endif()

set(SONNETDB_JAVAC_FFM_FLAGS --release "${SONNETDB_JAVA_FFM_RELEASE}")
set(SONNETDB_JAVA_FFM_FLAGS)
if(SONNETDB_JAVA_ENABLE_PREVIEW)
    list(APPEND SONNETDB_JAVAC_FFM_FLAGS --enable-preview)
    list(APPEND SONNETDB_JAVA_FFM_FLAGS --enable-preview)
endif()

if(SONNETDB_JAVA_BUILD_FFM)
    add_custom_command(
        OUTPUT "${SONNETDB_JAVA_JAR}"
        COMMAND "${CMAKE_COMMAND}" -E rm -rf "${SONNETDB_JAVA_CLASSES_DIR}" "${SONNETDB_JAVA_FFM_CLASSES_DIR}"
        COMMAND "${CMAKE_COMMAND}" -E make_directory "${SONNETDB_JAVA_CLASSES_DIR}" "${SONNETDB_JAVA_FFM_CLASSES_DIR}"
        COMMAND "${Java_JAVAC_EXECUTABLE}"
            ${SONNETDB_JAVAC_BASE_FLAGS}
            -d "${SONNETDB_JAVA_CLASSES_DIR}"
            ${SONNETDB_JAVA_SOURCE_FILES}
        COMMAND "${Java_JAVAC_EXECUTABLE}"
            ${SONNETDB_JAVAC_FFM_FLAGS}
            -cp "${SONNETDB_JAVA_CLASSES_DIR}"
            -d "${SONNETDB_JAVA_FFM_CLASSES_DIR}"
            ${SONNETDB_JAVA_FFM_SOURCE_FILES}
        COMMAND ${SONNETDB_JAVA_JAR_CREATE_COMMAND}
        COMMAND "${Java_JAR_EXECUTABLE}"
            --update
            --file "${SONNETDB_JAVA_JAR}"
            --release "${SONNETDB_JAVA_FFM_RELEASE}"
            -C "${SONNETDB_JAVA_FFM_CLASSES_DIR}" .
        DEPENDS ${SONNETDB_JAVA_SOURCE_FILES} ${SONNETDB_JAVA_FFM_SOURCE_FILES} "${SONNETDB_JAVA_MANIFEST}"
        COMMENT "Building Java 8 API jar with optional Java ${SONNETDB_JAVA_FFM_RELEASE} FFM backend"
        COMMAND_EXPAND_LISTS
        VERBATIM)
else()
    add_custom_command(
        OUTPUT "${SONNETDB_JAVA_JAR}"
        COMMAND "${CMAKE_COMMAND}" -E rm -rf "${SONNETDB_JAVA_CLASSES_DIR}"
        COMMAND "${CMAKE_COMMAND}" -E make_directory "${SONNETDB_JAVA_CLASSES_DIR}"
        COMMAND "${Java_JAVAC_EXECUTABLE}"
            ${SONNETDB_JAVAC_BASE_FLAGS}
            -d "${SONNETDB_JAVA_CLASSES_DIR}"
            ${SONNETDB_JAVA_SOURCE_FILES}
        COMMAND ${SONNETDB_JAVA_JAR_CREATE_COMMAND}
        DEPENDS ${SONNETDB_JAVA_SOURCE_FILES} "${SONNETDB_JAVA_MANIFEST}"
        COMMENT "Building Java 8 API jar with JNI backend"
        COMMAND_EXPAND_LISTS
        VERBATIM)
endif()

add_custom_target(sonnetdb_java ALL
    DEPENDS "${SONNETDB_JAVA_JAR}")

add_library(sonnetdb_java_native SHARED native/sonnetdb_jni.c)
target_include_directories(sonnetdb_java_native PRIVATE
    ${JNI_INCLUDE_DIRS}
    "${CMAKE_CURRENT_SOURCE_DIR}/../c/include")
set_target_properties(sonnetdb_java_native PROPERTIES
    OUTPUT_NAME "SonnetDB.Java.Native")
if(UNIX AND NOT APPLE)
    target_link_libraries(sonnetdb_java_native PRIVATE ${CMAKE_DL_LIBS})
endif()

if(SONNETDB_JAVA_BUILD_EXAMPLES)
    set(SONNETDB_JAVA_EXAMPLE_SOURCE "${CMAKE_CURRENT_SOURCE_DIR}/examples/Quickstart.java")
    add_custom_command(
        OUTPUT "${SONNETDB_JAVA_EXAMPLE_CLASSES_DIR}/com/sonnetdb/examples/Quickstart.class"
        COMMAND "${CMAKE_COMMAND}" -E rm -rf "${SONNETDB_JAVA_EXAMPLE_CLASSES_DIR}"
        COMMAND "${CMAKE_COMMAND}" -E make_directory "${SONNETDB_JAVA_EXAMPLE_CLASSES_DIR}"
        COMMAND "${Java_JAVAC_EXECUTABLE}"
            ${SONNETDB_JAVAC_BASE_FLAGS}
            -cp "${SONNETDB_JAVA_JAR}"
            -d "${SONNETDB_JAVA_EXAMPLE_CLASSES_DIR}"
            "${SONNETDB_JAVA_EXAMPLE_SOURCE}"
        DEPENDS "${SONNETDB_JAVA_JAR}" "${SONNETDB_JAVA_EXAMPLE_SOURCE}"
        COMMENT "Building Java quickstart example"
        COMMAND_EXPAND_LISTS
        VERBATIM)

    add_custom_target(sonnetdb_java_examples ALL
        DEPENDS "${SONNETDB_JAVA_EXAMPLE_CLASSES_DIR}/com/sonnetdb/examples/Quickstart.class")

    if(WIN32)
        set(SONNETDB_JAVA_CLASSPATH "$<SHELL_PATH:${SONNETDB_JAVA_JAR}>$<SEMICOLON>$<SHELL_PATH:${SONNETDB_JAVA_EXAMPLE_CLASSES_DIR}>")
    else()
        set(SONNETDB_JAVA_CLASSPATH "$<SHELL_PATH:${SONNETDB_JAVA_JAR}>:$<SHELL_PATH:${SONNETDB_JAVA_EXAMPLE_CLASSES_DIR}>")
    endif()

    if(SONNETDB_JAVA_NATIVE_LIBRARY)
        add_custom_target(run_sonnetdb_java_quickstart
            COMMAND "${Java_JAVA_EXECUTABLE}"
                "-Dsonnetdb.java.backend=jni"
                "-Dsonnetdb.jni.path=$<TARGET_FILE:sonnetdb_java_native>"
                "-Dsonnetdb.native.path=${SONNETDB_JAVA_NATIVE_LIBRARY}"
                -cp "${SONNETDB_JAVA_CLASSPATH}"
                com.sonnetdb.examples.Quickstart
            DEPENDS sonnetdb_java_examples sonnetdb_java_native
            COMMENT "Running Java quickstart example with JNI backend"
            VERBATIM)

        if(SONNETDB_JAVA_BUILD_FFM)
            add_custom_target(run_sonnetdb_java_quickstart_ffm
                COMMAND "${Java_JAVA_EXECUTABLE}"
                    ${SONNETDB_JAVA_FFM_FLAGS}
                    --enable-native-access=ALL-UNNAMED
                    "-Dsonnetdb.java.backend=ffm"
                    "-Dsonnetdb.native.path=${SONNETDB_JAVA_NATIVE_LIBRARY}"
                    -cp "${SONNETDB_JAVA_CLASSPATH}"
                    com.sonnetdb.examples.Quickstart
                DEPENDS sonnetdb_java_examples
                COMMENT "Running Java quickstart example with FFM backend"
                VERBATIM)
        endif()
    endif()
endif()

install(FILES "${SONNETDB_JAVA_JAR}" DESTINATION lib OPTIONAL)
install(TARGETS sonnetdb_java_native
    RUNTIME DESTINATION bin
    LIBRARY DESTINATION lib
    ARCHIVE DESTINATION lib)
