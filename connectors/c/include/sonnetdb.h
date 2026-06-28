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
typedef struct sonnetdb_doc sonnetdb_doc;
typedef struct sonnetdb_doc_result sonnetdb_doc_result;
typedef struct sonnetdb_kv sonnetdb_kv;
typedef struct sonnetdb_kv_entry sonnetdb_kv_entry;
typedef struct sonnetdb_kv_scan sonnetdb_kv_scan;
typedef struct sonnetdb_obj sonnetdb_obj;
typedef struct sonnetdb_obj_result sonnetdb_obj_result;
typedef struct sonnetdb_obj_writer sonnetdb_obj_writer;
typedef struct sonnetdb_obj_reader sonnetdb_obj_reader;
typedef struct sonnetdb_mq sonnetdb_mq;
typedef struct sonnetdb_mq_pull_result sonnetdb_mq_pull_result;
typedef struct sonnetdb_mq_result sonnetdb_mq_result;

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

SONNETDB_API sonnetdb_doc* sonnetdb_doc_open(sonnetdb_connection* connection, const char* collection);
SONNETDB_API void sonnetdb_doc_close(sonnetdb_doc* document);
SONNETDB_API sonnetdb_doc_result* sonnetdb_doc_create_collection(sonnetdb_doc* document, const char* options_json);
SONNETDB_API int32_t sonnetdb_doc_drop_collection(sonnetdb_doc* document);
SONNETDB_API sonnetdb_doc_result* sonnetdb_doc_insert(sonnetdb_doc* document, const char* payload_json);
SONNETDB_API sonnetdb_doc_result* sonnetdb_doc_update(sonnetdb_doc* document, const char* payload_json);
SONNETDB_API sonnetdb_doc_result* sonnetdb_doc_delete(sonnetdb_doc* document, const char* payload_json);
SONNETDB_API sonnetdb_doc_result* sonnetdb_doc_find_page(sonnetdb_doc* document, const char* payload_json);
SONNETDB_API sonnetdb_doc_result* sonnetdb_doc_aggregate(sonnetdb_doc* document, const char* payload_json);
SONNETDB_API void sonnetdb_doc_result_free(sonnetdb_doc_result* result);
SONNETDB_API int32_t sonnetdb_doc_result_json_length(sonnetdb_doc_result* result);
SONNETDB_API int32_t sonnetdb_doc_result_copy_json(sonnetdb_doc_result* result, char* buffer, int32_t buffer_length);

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

SONNETDB_API sonnetdb_obj* sonnetdb_obj_open(sonnetdb_connection* connection, const char* bucket);
SONNETDB_API void sonnetdb_obj_close(sonnetdb_obj* object_storage);
SONNETDB_API sonnetdb_obj_result* sonnetdb_obj_list_buckets(sonnetdb_obj* object_storage);
SONNETDB_API sonnetdb_obj_result* sonnetdb_obj_create_bucket(sonnetdb_obj* object_storage, const char* purpose);
SONNETDB_API int32_t sonnetdb_obj_delete_bucket(sonnetdb_obj* object_storage);
SONNETDB_API sonnetdb_obj_writer* sonnetdb_obj_writer_create(const char* content_type, const char* metadata_json, const char* tags_json);
SONNETDB_API int32_t sonnetdb_obj_writer_write(sonnetdb_obj_writer* writer, const void* buffer, int32_t buffer_length);
SONNETDB_API int64_t sonnetdb_obj_writer_length(sonnetdb_obj_writer* writer);
SONNETDB_API void sonnetdb_obj_writer_free(sonnetdb_obj_writer* writer);
SONNETDB_API sonnetdb_obj_result* sonnetdb_obj_put(sonnetdb_obj* object_storage, const char* key, sonnetdb_obj_writer* writer);
SONNETDB_API sonnetdb_obj_reader* sonnetdb_obj_get(sonnetdb_obj* object_storage, const char* key, int64_t offset, int64_t length);
SONNETDB_API sonnetdb_obj_result* sonnetdb_obj_head(sonnetdb_obj* object_storage, const char* key);
SONNETDB_API sonnetdb_obj_result* sonnetdb_obj_list(sonnetdb_obj* object_storage, const char* prefix, int32_t max_keys, const char* continuation_token);
SONNETDB_API sonnetdb_obj_result* sonnetdb_obj_delete(sonnetdb_obj* object_storage, const char* key);
SONNETDB_API sonnetdb_obj_result* sonnetdb_obj_delete_many(sonnetdb_obj* object_storage, const char* keys_json);
SONNETDB_API sonnetdb_obj_result* sonnetdb_obj_multipart_initiate(sonnetdb_obj* object_storage, const char* key, const char* content_type, const char* metadata_json, const char* tags_json);
SONNETDB_API sonnetdb_obj_result* sonnetdb_obj_multipart_upload_part(sonnetdb_obj* object_storage, const char* key, const char* upload_id, int32_t part_number, sonnetdb_obj_writer* writer);
SONNETDB_API sonnetdb_obj_result* sonnetdb_obj_multipart_complete(sonnetdb_obj* object_storage, const char* key, const char* upload_id, const char* part_numbers_json);
SONNETDB_API sonnetdb_obj_result* sonnetdb_obj_multipart_abort(sonnetdb_obj* object_storage, const char* key, const char* upload_id);
SONNETDB_API void sonnetdb_obj_result_free(sonnetdb_obj_result* result);
SONNETDB_API int32_t sonnetdb_obj_result_json_length(sonnetdb_obj_result* result);
SONNETDB_API int32_t sonnetdb_obj_result_copy_json(sonnetdb_obj_result* result, char* buffer, int32_t buffer_length);
SONNETDB_API void sonnetdb_obj_reader_free(sonnetdb_obj_reader* reader);
SONNETDB_API int32_t sonnetdb_obj_reader_read(sonnetdb_obj_reader* reader, void* buffer, int32_t buffer_length);
SONNETDB_API const char* sonnetdb_obj_reader_bucket(sonnetdb_obj_reader* reader);
SONNETDB_API const char* sonnetdb_obj_reader_key(sonnetdb_obj_reader* reader);
SONNETDB_API const char* sonnetdb_obj_reader_content_type(sonnetdb_obj_reader* reader);
SONNETDB_API const char* sonnetdb_obj_reader_etag(sonnetdb_obj_reader* reader);
SONNETDB_API const char* sonnetdb_obj_reader_sha256(sonnetdb_obj_reader* reader);
SONNETDB_API int64_t sonnetdb_obj_reader_size_bytes(sonnetdb_obj_reader* reader);
SONNETDB_API int64_t sonnetdb_obj_reader_offset(sonnetdb_obj_reader* reader);
SONNETDB_API int64_t sonnetdb_obj_reader_length(sonnetdb_obj_reader* reader);
SONNETDB_API int32_t sonnetdb_obj_reader_is_range(sonnetdb_obj_reader* reader);

SONNETDB_API sonnetdb_mq* sonnetdb_mq_open(sonnetdb_connection* connection, const char* topic);
SONNETDB_API void sonnetdb_mq_close(sonnetdb_mq* queue);
SONNETDB_API int64_t sonnetdb_mq_publish(sonnetdb_mq* queue, const void* payload, int32_t payload_length, const char* headers_json);
SONNETDB_API sonnetdb_mq_pull_result* sonnetdb_mq_pull(sonnetdb_mq* queue, const char* consumer_group, int32_t max_count);
SONNETDB_API int64_t sonnetdb_mq_ack(sonnetdb_mq* queue, const char* consumer_group, int64_t offset);
SONNETDB_API sonnetdb_mq_result* sonnetdb_mq_stats(sonnetdb_mq* queue);
SONNETDB_API void sonnetdb_mq_pull_result_free(sonnetdb_mq_pull_result* pull);
SONNETDB_API int32_t sonnetdb_mq_pull_result_message_count(sonnetdb_mq_pull_result* pull);
SONNETDB_API int32_t sonnetdb_mq_pull_next(sonnetdb_mq_pull_result* pull);
SONNETDB_API const char* sonnetdb_mq_pull_topic(sonnetdb_mq_pull_result* pull);
SONNETDB_API int64_t sonnetdb_mq_pull_offset(sonnetdb_mq_pull_result* pull);
SONNETDB_API int64_t sonnetdb_mq_pull_timestamp_unix_ms(sonnetdb_mq_pull_result* pull);
SONNETDB_API int32_t sonnetdb_mq_pull_headers_json_length(sonnetdb_mq_pull_result* pull);
SONNETDB_API int32_t sonnetdb_mq_pull_copy_headers_json(sonnetdb_mq_pull_result* pull, char* buffer, int32_t buffer_length);
SONNETDB_API int64_t sonnetdb_mq_pull_payload_length(sonnetdb_mq_pull_result* pull);
SONNETDB_API int32_t sonnetdb_mq_pull_copy_payload(sonnetdb_mq_pull_result* pull, void* buffer, int32_t buffer_length);
SONNETDB_API void sonnetdb_mq_result_free(sonnetdb_mq_result* result);
SONNETDB_API int32_t sonnetdb_mq_result_json_length(sonnetdb_mq_result* result);
SONNETDB_API int32_t sonnetdb_mq_result_copy_json(sonnetdb_mq_result* result, char* buffer, int32_t buffer_length);

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
