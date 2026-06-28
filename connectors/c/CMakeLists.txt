cmake_minimum_required(VERSION 3.21)

project(SonnetDBC
    VERSION 0.1.0
    DESCRIPTION "SonnetDB native C connector"
    LANGUAGES C)

set(SONNETDB_C_DOTNET_CONFIGURATION "Release" CACHE STRING "Configuration used by dotnet publish.")
set_property(CACHE SONNETDB_C_DOTNET_CONFIGURATION PROPERTY STRINGS Debug Release)

set(SONNETDB_C_RID "" CACHE STRING "Target .NET runtime identifier: win-x64, win-x86, win-arm64/win-xarm, or linux-x64.")
set_property(CACHE SONNETDB_C_RID PROPERTY STRINGS win-x64 win-x86 win-arm64 win-xarm linux-x64)

option(SONNETDB_C_BUILD_EXAMPLES "Build C connector examples." ON)

find_program(DOTNET_EXECUTABLE dotnet REQUIRED)

function(sonnetdb_detect_rid output_variable)
    if(SONNETDB_C_RID)
        set(_rid "${SONNETDB_C_RID}")
        if(_rid STREQUAL "win-xarm")
            set(_rid "win-arm64")
        endif()
    elseif(CMAKE_SYSTEM_NAME STREQUAL "Windows")
        string(TOLOWER "${CMAKE_VS_PLATFORM_NAME}" _platform)
        if(NOT _platform)
            string(TOLOWER "${CMAKE_SYSTEM_PROCESSOR}" _platform)
        endif()

        if(_platform MATCHES "^(arm64|aarch64)$")
            set(_rid "win-arm64")
        elseif(_platform MATCHES "^(win32|x86|i386|i686)$")
            set(_rid "win-x86")
        else()
            set(_rid "win-x64")
        endif()
    elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
        string(TOLOWER "${CMAKE_SYSTEM_PROCESSOR}" _processor)
        if(_processor MATCHES "^(x86_64|amd64)$")
            set(_rid "linux-x64")
        else()
            message(FATAL_ERROR "Only linux-x64 is currently supported by the C connector. Set SONNETDB_C_RID explicitly if adding a new RID.")
        endif()
    else()
        message(FATAL_ERROR "Unsupported platform '${CMAKE_SYSTEM_NAME}'. Supported RIDs: win-x64, win-x86, win-arm64, linux-x64.")
    endif()

    if(NOT _rid MATCHES "^(win-x64|win-x86|win-arm64|linux-x64)$")
        message(FATAL_ERROR "Unsupported SONNETDB_C_RID='${_rid}'. Supported RIDs: win-x64, win-x86, win-arm64/win-xarm, linux-x64.")
    endif()

    set(${output_variable} "${_rid}" PARENT_SCOPE)
endfunction()

sonnetdb_detect_rid(SONNETDB_C_EFFECTIVE_RID)
message(STATUS "SonnetDB C connector RID: ${SONNETDB_C_EFFECTIVE_RID}")

set(SONNETDB_C_INCLUDE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/include")
set(SONNETDB_REPO_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../..")
set(SONNETDB_NATIVE_PROJECT "${CMAKE_CURRENT_SOURCE_DIR}/native/SonnetDB.Native/SonnetDB.Native.csproj")
set(SONNETDB_NATIVE_PROJECT_DIR "${CMAKE_CURRENT_SOURCE_DIR}/native/SonnetDB.Native")
set(SONNETDB_NATIVE_PUBLISH_DIR "${CMAKE_CURRENT_BINARY_DIR}/native/${SONNETDB_C_EFFECTIVE_RID}/publish")
set(SONNETDB_NATIVE_STAMP "${CMAKE_CURRENT_BINARY_DIR}/native/${SONNETDB_C_EFFECTIVE_RID}/sonnetdb-native.stamp")

if(WIN32)
    set(SONNETDB_NATIVE_LIBRARY "${SONNETDB_NATIVE_PUBLISH_DIR}/SonnetDB.Native.dll")
    set(SONNETDB_NATIVE_IMPORT_LIBRARY "${SONNETDB_NATIVE_PROJECT_DIR}/bin/${SONNETDB_C_DOTNET_CONFIGURATION}/net10.0/${SONNETDB_C_EFFECTIVE_RID}/native/SonnetDB.Native.lib")
elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    set(SONNETDB_NATIVE_LIBRARY "${SONNETDB_NATIVE_PUBLISH_DIR}/SonnetDB.Native.so")
else()
    message(FATAL_ERROR "Unsupported CMake system '${CMAKE_SYSTEM_NAME}'.")
endif()

file(GLOB_RECURSE SONNETDB_NATIVE_SOURCE_FILES CONFIGURE_DEPENDS
    "${SONNETDB_NATIVE_PROJECT_DIR}/*.cs"
    "${SONNETDB_REPO_ROOT}/src/SonnetDB.Core/*.cs"
    "${SONNETDB_REPO_ROOT}/src/SonnetDB.Data/*.cs")

set(SONNETDB_DOTNET_BUILD_FILES
    "${SONNETDB_REPO_ROOT}/Directory.Build.props"
    "${SONNETDB_REPO_ROOT}/Directory.Packages.props"
    "${SONNETDB_NATIVE_PROJECT}"
    "${SONNETDB_REPO_ROOT}/src/SonnetDB.Core/SonnetDB.Core.csproj"
    "${SONNETDB_REPO_ROOT}/src/SonnetDB.Data/SonnetDB.Data.csproj")

add_custom_command(
    OUTPUT "${SONNETDB_NATIVE_STAMP}"
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${SONNETDB_NATIVE_PUBLISH_DIR}"
    COMMAND "${DOTNET_EXECUTABLE}" publish "${SONNETDB_NATIVE_PROJECT}"
        -c "${SONNETDB_C_DOTNET_CONFIGURATION}"
        -r "${SONNETDB_C_EFFECTIVE_RID}"
        -o "${SONNETDB_NATIVE_PUBLISH_DIR}"
        /p:PublishAot=true
        /p:NativeLib=Shared
    COMMAND "${CMAKE_COMMAND}" -E touch "${SONNETDB_NATIVE_STAMP}"
    DEPENDS ${SONNETDB_DOTNET_BUILD_FILES} ${SONNETDB_NATIVE_SOURCE_FILES}
    COMMENT "Publishing SonnetDB.Native for ${SONNETDB_C_EFFECTIVE_RID}"
    VERBATIM)

add_custom_target(sonnetdb_native_publish ALL
    DEPENDS "${SONNETDB_NATIVE_STAMP}")

if(WIN32)
    add_library(sonnetdb_c SHARED IMPORTED GLOBAL)
    add_dependencies(sonnetdb_c sonnetdb_native_publish)
    set_target_properties(sonnetdb_c PROPERTIES
        IMPORTED_LOCATION "${SONNETDB_NATIVE_LIBRARY}"
        INTERFACE_INCLUDE_DIRECTORIES "${SONNETDB_C_INCLUDE_DIR}"
        IMPORTED_IMPLIB "${SONNETDB_NATIVE_IMPORT_LIBRARY}")
elseif(UNIX AND NOT APPLE)
    add_library(sonnetdb_c INTERFACE)
    add_dependencies(sonnetdb_c sonnetdb_native_publish)
    target_include_directories(sonnetdb_c INTERFACE "${SONNETDB_C_INCLUDE_DIR}")
    target_link_directories(sonnetdb_c INTERFACE "${SONNETDB_NATIVE_PUBLISH_DIR}")
    target_link_libraries(sonnetdb_c INTERFACE "-l:SonnetDB.Native.so")
endif()

if(SONNETDB_C_BUILD_EXAMPLES)
    add_executable(sonnetdb_quickstart examples/quickstart.c)
    target_compile_features(sonnetdb_quickstart PRIVATE c_std_99)
    target_link_libraries(sonnetdb_quickstart PRIVATE sonnetdb_c)
    if(UNIX AND NOT APPLE)
        add_dependencies(sonnetdb_quickstart sonnetdb_native_publish)
    endif()

    add_custom_command(TARGET sonnetdb_quickstart POST_BUILD
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different
            "${SONNETDB_NATIVE_LIBRARY}"
            "$<TARGET_FILE_DIR:sonnetdb_quickstart>"
        COMMENT "Copying SonnetDB native library next to sonnetdb_quickstart"
        VERBATIM)

    if(UNIX AND NOT APPLE)
        set_target_properties(sonnetdb_quickstart PROPERTIES
            BUILD_RPATH "$ORIGIN")
    endif()
endif()

install(FILES "${SONNETDB_C_INCLUDE_DIR}/sonnetdb.h" DESTINATION include)
install(FILES "${SONNETDB_NATIVE_LIBRARY}" DESTINATION lib OPTIONAL)
