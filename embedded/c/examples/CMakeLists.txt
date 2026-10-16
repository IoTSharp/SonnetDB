cmake_minimum_required(VERSION 3.21)
project(SonnetDBEmbeddedResultLifetime LANGUAGES C)

set(SONNETDB_PACKAGE_DIR "" CACHE PATH "Extracted embedded C engineering package directory.")
if(NOT SONNETDB_PACKAGE_DIR)
    message(FATAL_ERROR "Set SONNETDB_PACKAGE_DIR to a package containing include/ and lib/.")
endif()

add_library(sonnetdb_embedded SHARED IMPORTED)
set_target_properties(sonnetdb_embedded PROPERTIES
    INTERFACE_INCLUDE_DIRECTORIES "${SONNETDB_PACKAGE_DIR}/include")
if(WIN32)
    set_target_properties(sonnetdb_embedded PROPERTIES
        IMPORTED_LOCATION "${SONNETDB_PACKAGE_DIR}/lib/SonnetDB.Native.dll"
        IMPORTED_IMPLIB "${SONNETDB_PACKAGE_DIR}/lib/SonnetDB.Native.lib")
elseif(APPLE)
    set_target_properties(sonnetdb_embedded PROPERTIES
        IMPORTED_LOCATION "${SONNETDB_PACKAGE_DIR}/lib/SonnetDB.Native.dylib")
else()
    set_target_properties(sonnetdb_embedded PROPERTIES
        IMPORTED_LOCATION "${SONNETDB_PACKAGE_DIR}/lib/SonnetDB.Native.so")
endif()

add_executable(sonnetdb_result_lifetime result_lifetime.c)
target_compile_features(sonnetdb_result_lifetime PRIVATE c_std_99)
target_link_libraries(sonnetdb_result_lifetime PRIVATE sonnetdb_embedded)
add_custom_command(TARGET sonnetdb_result_lifetime POST_BUILD
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different
        "$<TARGET_FILE:sonnetdb_embedded>"
        "$<TARGET_FILE_DIR:sonnetdb_result_lifetime>"
    VERBATIM)
if(APPLE)
    set_target_properties(sonnetdb_result_lifetime PROPERTIES BUILD_RPATH "@loader_path")
elseif(UNIX)
    set_target_properties(sonnetdb_result_lifetime PROPERTIES BUILD_RPATH "$ORIGIN")
endif()
