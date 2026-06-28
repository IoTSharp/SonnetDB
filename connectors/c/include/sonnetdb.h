#ifndef SONNETDB_H
#define SONNETDB_H

#include <stdint.h>

#ifdef _WIN32
#  define SONNETDB_API __declspec(dllimport)
#else
#  define SONNETDB_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct sonnetdb_connection sonnetdb_connection;
typedef struct sonnetdb_result sonnetdb_result;
typedef struct sonnetdb_bulk sonnetdb_bulk;

typedef enum sonnetdb_value_type {
    SONNETDB_TYPE_NULL = 0,
    SONNETDB_TYPE_INT64 = 1,
    SONNETDB_TYPE_DOUBLE = 2,
    SONNETDB_TYPE_BOOL = 3,
    SONNETDB_TYPE_TEXT = 4
} sonnetdb_value_type;

SONNETDB_API sonnetdb_connection* sonnetdb_open(const char* data_source);
SONNETDB_API void sonnetdb_close(sonnetdb_connection* connection);

SONNETDB_API sonnetdb_result* sonnetdb_execute(sonnetdb_connection* connection, const char* sql);
SONNETDB_API void sonnetdb_result_free(sonnetdb_result* result);

SONNETDB_API sonnetdb_bulk* sonnetdb_bulk_create(const char* payload);
SONNETDB_API int32_t sonnetdb_bulk_set_measurement(sonnetdb_bulk* bulk, const char* measurement);
SONNETDB_API int32_t sonnetdb_bulk_set_onerror(sonnetdb_bulk* bulk, const char* onerror);
SONNETDB_API int32_t sonnetdb_bulk_set_flush(sonnetdb_bulk* bulk, const char* flush);
SONNETDB_API sonnetdb_result* sonnetdb_bulk_execute(sonnetdb_connection* connection, sonnetdb_bulk* bulk);
SONNETDB_API void sonnetdb_bulk_free(sonnetdb_bulk* bulk);

SONNETDB_API int32_t sonnetdb_result_records_affected(sonnetdb_result* result);
SONNETDB_API int32_t sonnetdb_result_column_count(sonnetdb_result* result);
SONNETDB_API const char* sonnetdb_result_column_name(sonnetdb_result* result, int32_t ordinal);
SONNETDB_API int32_t sonnetdb_result_next(sonnetdb_result* result);

SONNETDB_API sonnetdb_value_type sonnetdb_result_value_type(sonnetdb_result* result, int32_t ordinal);
SONNETDB_API int64_t sonnetdb_result_value_int64(sonnetdb_result* result, int32_t ordinal);
SONNETDB_API double sonnetdb_result_value_double(sonnetdb_result* result, int32_t ordinal);
SONNETDB_API int32_t sonnetdb_result_value_bool(sonnetdb_result* result, int32_t ordinal);
SONNETDB_API const char* sonnetdb_result_value_text(sonnetdb_result* result, int32_t ordinal);

SONNETDB_API int32_t sonnetdb_flush(sonnetdb_connection* connection);
SONNETDB_API int32_t sonnetdb_version(char* buffer, int32_t buffer_length);
SONNETDB_API int32_t sonnetdb_last_error(char* buffer, int32_t buffer_length);

#ifdef __cplusplus
}
#endif

#endif
