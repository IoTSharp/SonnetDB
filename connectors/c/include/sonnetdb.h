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
typedef struct sonnetdb_kv sonnetdb_kv;
typedef struct sonnetdb_kv_entry sonnetdb_kv_entry;
typedef struct sonnetdb_kv_scan sonnetdb_kv_scan;

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

SONNETDB_API sonnetdb_kv* sonnetdb_kv_open(sonnetdb_connection* connection, const char* keyspace, const char* ns);
SONNETDB_API void sonnetdb_kv_close(sonnetdb_kv* kv);
SONNETDB_API sonnetdb_kv_entry* sonnetdb_kv_get(sonnetdb_kv* kv, const char* key);
SONNETDB_API int64_t sonnetdb_kv_set(sonnetdb_kv* kv, const char* key, const void* value, int32_t value_length, int64_t expires_at_unix_ms);
SONNETDB_API int32_t sonnetdb_kv_delete(sonnetdb_kv* kv, const char* key);
SONNETDB_API sonnetdb_kv_scan* sonnetdb_kv_scan_prefix(sonnetdb_kv* kv, const char* prefix, int32_t limit);
SONNETDB_API int64_t sonnetdb_kv_ttl(sonnetdb_kv* kv, const char* key, int64_t* expires_at_unix_ms);
SONNETDB_API int32_t sonnetdb_kv_expire_at(sonnetdb_kv* kv, const char* key, int64_t expires_at_unix_ms);
SONNETDB_API int32_t sonnetdb_kv_persist(sonnetdb_kv* kv, const char* key);
SONNETDB_API int32_t sonnetdb_kv_incr(sonnetdb_kv* kv, const char* key, int64_t delta, int64_t* value, int64_t* version);
SONNETDB_API int32_t sonnetdb_kv_cas(sonnetdb_kv* kv, const char* key, int64_t expected_version, const void* value, int32_t value_length, int64_t expires_at_unix_ms, int64_t* current_version, int64_t* new_version);
SONNETDB_API void sonnetdb_kv_entry_free(sonnetdb_kv_entry* entry);
SONNETDB_API const char* sonnetdb_kv_entry_key(sonnetdb_kv_entry* entry);
SONNETDB_API int64_t sonnetdb_kv_entry_value_length(sonnetdb_kv_entry* entry);
SONNETDB_API int32_t sonnetdb_kv_entry_copy_value(sonnetdb_kv_entry* entry, void* buffer, int32_t buffer_length);
SONNETDB_API int64_t sonnetdb_kv_entry_version(sonnetdb_kv_entry* entry);
SONNETDB_API int64_t sonnetdb_kv_entry_expires_at_unix_ms(sonnetdb_kv_entry* entry);
SONNETDB_API int32_t sonnetdb_kv_scan_next(sonnetdb_kv_scan* scan);
SONNETDB_API const char* sonnetdb_kv_scan_key(sonnetdb_kv_scan* scan);
SONNETDB_API int64_t sonnetdb_kv_scan_value_length(sonnetdb_kv_scan* scan);
SONNETDB_API int32_t sonnetdb_kv_scan_copy_value(sonnetdb_kv_scan* scan, void* buffer, int32_t buffer_length);
SONNETDB_API int64_t sonnetdb_kv_scan_version(sonnetdb_kv_scan* scan);
SONNETDB_API int64_t sonnetdb_kv_scan_expires_at_unix_ms(sonnetdb_kv_scan* scan);
SONNETDB_API void sonnetdb_kv_scan_free(sonnetdb_kv_scan* scan);

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
