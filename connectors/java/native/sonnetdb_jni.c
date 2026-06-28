#include <jni.h>
#include <limits.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#else
#include <dlfcn.h>
#endif

typedef void* (*sonnetdb_open_fn)(const char*);
typedef void (*sonnetdb_close_fn)(void*);
typedef void* (*sonnetdb_execute_fn)(void*, const char*);
typedef void* (*sonnetdb_bulk_create_fn)(const char*);
typedef int32_t (*sonnetdb_bulk_set_measurement_fn)(void*, const char*);
typedef int32_t (*sonnetdb_bulk_set_onerror_fn)(void*, const char*);
typedef int32_t (*sonnetdb_bulk_set_flush_fn)(void*, const char*);
typedef void* (*sonnetdb_bulk_execute_fn)(void*, void*);
typedef void (*sonnetdb_bulk_free_fn)(void*);
typedef void (*sonnetdb_result_free_fn)(void*);
typedef int32_t (*sonnetdb_result_records_affected_fn)(void*);
typedef int32_t (*sonnetdb_result_column_count_fn)(void*);
typedef const char* (*sonnetdb_result_column_name_fn)(void*, int32_t);
typedef int32_t (*sonnetdb_result_next_fn)(void*);
typedef int32_t (*sonnetdb_result_value_type_fn)(void*, int32_t);
typedef int64_t (*sonnetdb_result_value_int64_fn)(void*, int32_t);
typedef double (*sonnetdb_result_value_double_fn)(void*, int32_t);
typedef int32_t (*sonnetdb_result_value_bool_fn)(void*, int32_t);
typedef const char* (*sonnetdb_result_value_text_fn)(void*, int32_t);
typedef void* (*sonnetdb_kv_open_fn)(void*, const char*, const char*);
typedef void (*sonnetdb_kv_close_fn)(void*);
typedef void* (*sonnetdb_kv_get_fn)(void*, const char*);
typedef int64_t (*sonnetdb_kv_set_fn)(void*, const char*, const void*, int32_t, int64_t);
typedef int32_t (*sonnetdb_kv_delete_fn)(void*, const char*);
typedef void* (*sonnetdb_kv_scan_prefix_fn)(void*, const char*, int32_t);
typedef int64_t (*sonnetdb_kv_ttl_fn)(void*, const char*, int64_t*);
typedef int32_t (*sonnetdb_kv_expire_at_fn)(void*, const char*, int64_t);
typedef int32_t (*sonnetdb_kv_persist_fn)(void*, const char*);
typedef int32_t (*sonnetdb_kv_incr_fn)(void*, const char*, int64_t, int64_t*, int64_t*);
typedef int32_t (*sonnetdb_kv_cas_fn)(void*, const char*, int64_t, const void*, int32_t, int64_t, int64_t*, int64_t*);
typedef void (*sonnetdb_kv_entry_free_fn)(void*);
typedef const char* (*sonnetdb_kv_entry_key_fn)(void*);
typedef int64_t (*sonnetdb_kv_entry_value_length_fn)(void*);
typedef int32_t (*sonnetdb_kv_entry_copy_value_fn)(void*, void*, int32_t);
typedef int64_t (*sonnetdb_kv_entry_version_fn)(void*);
typedef int64_t (*sonnetdb_kv_entry_expires_at_unix_ms_fn)(void*);
typedef int32_t (*sonnetdb_kv_scan_next_fn)(void*);
typedef const char* (*sonnetdb_kv_scan_key_fn)(void*);
typedef int64_t (*sonnetdb_kv_scan_value_length_fn)(void*);
typedef int32_t (*sonnetdb_kv_scan_copy_value_fn)(void*, void*, int32_t);
typedef int64_t (*sonnetdb_kv_scan_version_fn)(void*);
typedef int64_t (*sonnetdb_kv_scan_expires_at_unix_ms_fn)(void*);
typedef void (*sonnetdb_kv_scan_free_fn)(void*);
typedef void* (*sonnetdb_doc_open_fn)(void*, const char*);
typedef void (*sonnetdb_doc_close_fn)(void*);
typedef void* (*sonnetdb_doc_create_collection_fn)(void*, const char*);
typedef int32_t (*sonnetdb_doc_drop_collection_fn)(void*);
typedef void* (*sonnetdb_doc_json_fn)(void*, const char*);
typedef void (*sonnetdb_doc_result_free_fn)(void*);
typedef int32_t (*sonnetdb_doc_result_json_length_fn)(void*);
typedef int32_t (*sonnetdb_doc_result_copy_json_fn)(void*, char*, int32_t);
typedef int32_t (*sonnetdb_flush_fn)(void*);
typedef int32_t (*sonnetdb_version_fn)(char*, int32_t);
typedef int32_t (*sonnetdb_last_error_fn)(char*, int32_t);

static sonnetdb_open_fn p_sonnetdb_open;
static sonnetdb_close_fn p_sonnetdb_close;
static sonnetdb_execute_fn p_sonnetdb_execute;
static sonnetdb_bulk_create_fn p_sonnetdb_bulk_create;
static sonnetdb_bulk_set_measurement_fn p_sonnetdb_bulk_set_measurement;
static sonnetdb_bulk_set_onerror_fn p_sonnetdb_bulk_set_onerror;
static sonnetdb_bulk_set_flush_fn p_sonnetdb_bulk_set_flush;
static sonnetdb_bulk_execute_fn p_sonnetdb_bulk_execute;
static sonnetdb_bulk_free_fn p_sonnetdb_bulk_free;
static sonnetdb_result_free_fn p_sonnetdb_result_free;
static sonnetdb_result_records_affected_fn p_sonnetdb_result_records_affected;
static sonnetdb_result_column_count_fn p_sonnetdb_result_column_count;
static sonnetdb_result_column_name_fn p_sonnetdb_result_column_name;
static sonnetdb_result_next_fn p_sonnetdb_result_next;
static sonnetdb_result_value_type_fn p_sonnetdb_result_value_type;
static sonnetdb_result_value_int64_fn p_sonnetdb_result_value_int64;
static sonnetdb_result_value_double_fn p_sonnetdb_result_value_double;
static sonnetdb_result_value_bool_fn p_sonnetdb_result_value_bool;
static sonnetdb_result_value_text_fn p_sonnetdb_result_value_text;
static sonnetdb_kv_open_fn p_sonnetdb_kv_open;
static sonnetdb_kv_close_fn p_sonnetdb_kv_close;
static sonnetdb_kv_get_fn p_sonnetdb_kv_get;
static sonnetdb_kv_set_fn p_sonnetdb_kv_set;
static sonnetdb_kv_delete_fn p_sonnetdb_kv_delete;
static sonnetdb_kv_scan_prefix_fn p_sonnetdb_kv_scan_prefix;
static sonnetdb_kv_ttl_fn p_sonnetdb_kv_ttl;
static sonnetdb_kv_expire_at_fn p_sonnetdb_kv_expire_at;
static sonnetdb_kv_persist_fn p_sonnetdb_kv_persist;
static sonnetdb_kv_incr_fn p_sonnetdb_kv_incr;
static sonnetdb_kv_cas_fn p_sonnetdb_kv_cas;
static sonnetdb_kv_entry_free_fn p_sonnetdb_kv_entry_free;
static sonnetdb_kv_entry_key_fn p_sonnetdb_kv_entry_key;
static sonnetdb_kv_entry_value_length_fn p_sonnetdb_kv_entry_value_length;
static sonnetdb_kv_entry_copy_value_fn p_sonnetdb_kv_entry_copy_value;
static sonnetdb_kv_entry_version_fn p_sonnetdb_kv_entry_version;
static sonnetdb_kv_entry_expires_at_unix_ms_fn p_sonnetdb_kv_entry_expires_at_unix_ms;
static sonnetdb_kv_scan_next_fn p_sonnetdb_kv_scan_next;
static sonnetdb_kv_scan_key_fn p_sonnetdb_kv_scan_key;
static sonnetdb_kv_scan_value_length_fn p_sonnetdb_kv_scan_value_length;
static sonnetdb_kv_scan_copy_value_fn p_sonnetdb_kv_scan_copy_value;
static sonnetdb_kv_scan_version_fn p_sonnetdb_kv_scan_version;
static sonnetdb_kv_scan_expires_at_unix_ms_fn p_sonnetdb_kv_scan_expires_at_unix_ms;
static sonnetdb_kv_scan_free_fn p_sonnetdb_kv_scan_free;
static sonnetdb_doc_open_fn p_sonnetdb_doc_open;
static sonnetdb_doc_close_fn p_sonnetdb_doc_close;
static sonnetdb_doc_create_collection_fn p_sonnetdb_doc_create_collection;
static sonnetdb_doc_drop_collection_fn p_sonnetdb_doc_drop_collection;
static sonnetdb_doc_json_fn p_sonnetdb_doc_insert;
static sonnetdb_doc_json_fn p_sonnetdb_doc_update;
static sonnetdb_doc_json_fn p_sonnetdb_doc_delete;
static sonnetdb_doc_json_fn p_sonnetdb_doc_find_page;
static sonnetdb_doc_json_fn p_sonnetdb_doc_aggregate;
static sonnetdb_doc_result_free_fn p_sonnetdb_doc_result_free;
static sonnetdb_doc_result_json_length_fn p_sonnetdb_doc_result_json_length;
static sonnetdb_doc_result_copy_json_fn p_sonnetdb_doc_result_copy_json;
static sonnetdb_flush_fn p_sonnetdb_flush;
static sonnetdb_version_fn p_sonnetdb_version;
static sonnetdb_last_error_fn p_sonnetdb_last_error;

#ifdef _WIN32
static HMODULE native_library;
#else
static void* native_library;
#endif

static void throw_sonnet(JNIEnv* env, const char* message)
{
    jclass ex = (*env)->FindClass(env, "com/sonnetdb/SonnetDbException");
    if (ex == NULL)
    {
        ex = (*env)->FindClass(env, "java/lang/RuntimeException");
    }
    if (ex != NULL)
    {
        (*env)->ThrowNew(env, ex, message == NULL ? "SonnetDB JNI error." : message);
    }
}

static void throw_last_error(JNIEnv* env, const char* fallback)
{
    char buffer[4096];
    buffer[0] = '\0';
    if (p_sonnetdb_last_error != NULL)
    {
        p_sonnetdb_last_error(buffer, (int32_t)sizeof(buffer));
    }
    if (buffer[0] == '\0')
    {
        throw_sonnet(env, fallback);
    }
    else
    {
        throw_sonnet(env, buffer);
    }
}

static jbyteArray copy_bytes(JNIEnv* env, void* handle, int64_t (*length_fn)(void*), int32_t (*copy_fn)(void*, void*, int32_t))
{
    int64_t required = length_fn(handle);
    if (required < 0 || required > INT32_MAX)
    {
        throw_last_error(env, "KV value length is invalid.");
        return NULL;
    }

    jbyteArray array = (*env)->NewByteArray(env, (jsize)required);
    if (array == NULL)
    {
        return NULL;
    }
    if (required == 0)
    {
        return array;
    }

    jbyte* bytes = (*env)->GetByteArrayElements(env, array, NULL);
    if (bytes == NULL)
    {
        return NULL;
    }
    int32_t copied = copy_fn(handle, bytes, (int32_t)required);
    (*env)->ReleaseByteArrayElements(env, array, bytes, 0);
    if (copied < 0)
    {
        throw_last_error(env, "KV value copy failed.");
        return NULL;
    }
    return array;
}

static jstring copy_doc_json(JNIEnv* env, void* result, const char* fallback)
{
    int32_t required = p_sonnetdb_doc_result_json_length(result);
    if (required < 0)
    {
        throw_last_error(env, fallback);
        return NULL;
    }

    char* buffer = (char*)malloc((size_t)required + 1);
    if (buffer == NULL)
    {
        throw_sonnet(env, "Out of memory.");
        return NULL;
    }

    int32_t copied = p_sonnetdb_doc_result_copy_json(result, buffer, required + 1);
    if (copied < 0)
    {
        free(buffer);
        throw_last_error(env, fallback);
        return NULL;
    }

    jstring value = (*env)->NewStringUTF(env, buffer);
    free(buffer);
    return value;
}

static void* byte_array_elements(JNIEnv* env, jbyteArray value, jbyte** bytes, jsize* length)
{
    *bytes = NULL;
    *length = 0;
    if (value == NULL)
    {
        throw_sonnet(env, "KV value cannot be null.");
        return NULL;
    }

    *length = (*env)->GetArrayLength(env, value);
    if (*length == 0)
    {
        return NULL;
    }

    *bytes = (*env)->GetByteArrayElements(env, value, NULL);
    return *bytes;
}

#ifdef _WIN32
static void* resolve_symbol(const char* name)
{
    return (void*)GetProcAddress(native_library, name);
}

static int load_library(JNIEnv* env, jstring path)
{
    if (path != NULL)
    {
        const jchar* chars = (*env)->GetStringChars(env, path, NULL);
        if (chars == NULL)
        {
            return 0;
        }
        native_library = LoadLibraryW((LPCWSTR)chars);
        (*env)->ReleaseStringChars(env, path, chars);
    }
    else
    {
        native_library = LoadLibraryW(L"SonnetDB.Native.dll");
    }

    if (native_library == NULL)
    {
        throw_sonnet(env, "Cannot load SonnetDB.Native.dll. Set sonnetdb.native.path.");
        return 0;
    }
    return 1;
}
#else
static void* resolve_symbol(const char* name)
{
    return dlsym(native_library, name);
}

static int load_library(JNIEnv* env, jstring path)
{
    if (path != NULL)
    {
        const char* chars = (*env)->GetStringUTFChars(env, path, NULL);
        if (chars == NULL)
        {
            return 0;
        }
        native_library = dlopen(chars, RTLD_NOW | RTLD_LOCAL);
        (*env)->ReleaseStringUTFChars(env, path, chars);
    }
    else
    {
        native_library = dlopen("SonnetDB.Native.so", RTLD_NOW | RTLD_LOCAL);
    }

    if (native_library == NULL)
    {
        const char* error = dlerror();
        throw_sonnet(env, error == NULL ? "Cannot load SonnetDB.Native.so. Set sonnetdb.native.path." : error);
        return 0;
    }
    return 1;
}
#endif

#define RESOLVE_SYMBOL(field, type, name) \
    do { \
        field = (type)resolve_symbol(name); \
        if (field == NULL) { \
            throw_sonnet(env, "Cannot resolve SonnetDB native symbol: " name); \
            return; \
        } \
    } while (0)

JNIEXPORT void JNICALL Java_com_sonnetdb_jni_SonnetDbJni_initialize(
    JNIEnv* env,
    jclass cls,
    jstring nativeLibraryPath)
{
    (void)cls;

    if (native_library != NULL)
    {
        return;
    }

    if (!load_library(env, nativeLibraryPath))
    {
        return;
    }

    RESOLVE_SYMBOL(p_sonnetdb_open, sonnetdb_open_fn, "sonnetdb_open");
    RESOLVE_SYMBOL(p_sonnetdb_close, sonnetdb_close_fn, "sonnetdb_close");
    RESOLVE_SYMBOL(p_sonnetdb_execute, sonnetdb_execute_fn, "sonnetdb_execute");
    RESOLVE_SYMBOL(p_sonnetdb_bulk_create, sonnetdb_bulk_create_fn, "sonnetdb_bulk_create");
    RESOLVE_SYMBOL(p_sonnetdb_bulk_set_measurement, sonnetdb_bulk_set_measurement_fn, "sonnetdb_bulk_set_measurement");
    RESOLVE_SYMBOL(p_sonnetdb_bulk_set_onerror, sonnetdb_bulk_set_onerror_fn, "sonnetdb_bulk_set_onerror");
    RESOLVE_SYMBOL(p_sonnetdb_bulk_set_flush, sonnetdb_bulk_set_flush_fn, "sonnetdb_bulk_set_flush");
    RESOLVE_SYMBOL(p_sonnetdb_bulk_execute, sonnetdb_bulk_execute_fn, "sonnetdb_bulk_execute");
    RESOLVE_SYMBOL(p_sonnetdb_bulk_free, sonnetdb_bulk_free_fn, "sonnetdb_bulk_free");
    RESOLVE_SYMBOL(p_sonnetdb_result_free, sonnetdb_result_free_fn, "sonnetdb_result_free");
    RESOLVE_SYMBOL(p_sonnetdb_result_records_affected, sonnetdb_result_records_affected_fn, "sonnetdb_result_records_affected");
    RESOLVE_SYMBOL(p_sonnetdb_result_column_count, sonnetdb_result_column_count_fn, "sonnetdb_result_column_count");
    RESOLVE_SYMBOL(p_sonnetdb_result_column_name, sonnetdb_result_column_name_fn, "sonnetdb_result_column_name");
    RESOLVE_SYMBOL(p_sonnetdb_result_next, sonnetdb_result_next_fn, "sonnetdb_result_next");
    RESOLVE_SYMBOL(p_sonnetdb_result_value_type, sonnetdb_result_value_type_fn, "sonnetdb_result_value_type");
    RESOLVE_SYMBOL(p_sonnetdb_result_value_int64, sonnetdb_result_value_int64_fn, "sonnetdb_result_value_int64");
    RESOLVE_SYMBOL(p_sonnetdb_result_value_double, sonnetdb_result_value_double_fn, "sonnetdb_result_value_double");
    RESOLVE_SYMBOL(p_sonnetdb_result_value_bool, sonnetdb_result_value_bool_fn, "sonnetdb_result_value_bool");
    RESOLVE_SYMBOL(p_sonnetdb_result_value_text, sonnetdb_result_value_text_fn, "sonnetdb_result_value_text");
    RESOLVE_SYMBOL(p_sonnetdb_kv_open, sonnetdb_kv_open_fn, "sonnetdb_kv_open");
    RESOLVE_SYMBOL(p_sonnetdb_kv_close, sonnetdb_kv_close_fn, "sonnetdb_kv_close");
    RESOLVE_SYMBOL(p_sonnetdb_kv_get, sonnetdb_kv_get_fn, "sonnetdb_kv_get");
    RESOLVE_SYMBOL(p_sonnetdb_kv_set, sonnetdb_kv_set_fn, "sonnetdb_kv_set");
    RESOLVE_SYMBOL(p_sonnetdb_kv_delete, sonnetdb_kv_delete_fn, "sonnetdb_kv_delete");
    RESOLVE_SYMBOL(p_sonnetdb_kv_scan_prefix, sonnetdb_kv_scan_prefix_fn, "sonnetdb_kv_scan_prefix");
    RESOLVE_SYMBOL(p_sonnetdb_kv_ttl, sonnetdb_kv_ttl_fn, "sonnetdb_kv_ttl");
    RESOLVE_SYMBOL(p_sonnetdb_kv_expire_at, sonnetdb_kv_expire_at_fn, "sonnetdb_kv_expire_at");
    RESOLVE_SYMBOL(p_sonnetdb_kv_persist, sonnetdb_kv_persist_fn, "sonnetdb_kv_persist");
    RESOLVE_SYMBOL(p_sonnetdb_kv_incr, sonnetdb_kv_incr_fn, "sonnetdb_kv_incr");
    RESOLVE_SYMBOL(p_sonnetdb_kv_cas, sonnetdb_kv_cas_fn, "sonnetdb_kv_cas");
    RESOLVE_SYMBOL(p_sonnetdb_kv_entry_free, sonnetdb_kv_entry_free_fn, "sonnetdb_kv_entry_free");
    RESOLVE_SYMBOL(p_sonnetdb_kv_entry_key, sonnetdb_kv_entry_key_fn, "sonnetdb_kv_entry_key");
    RESOLVE_SYMBOL(p_sonnetdb_kv_entry_value_length, sonnetdb_kv_entry_value_length_fn, "sonnetdb_kv_entry_value_length");
    RESOLVE_SYMBOL(p_sonnetdb_kv_entry_copy_value, sonnetdb_kv_entry_copy_value_fn, "sonnetdb_kv_entry_copy_value");
    RESOLVE_SYMBOL(p_sonnetdb_kv_entry_version, sonnetdb_kv_entry_version_fn, "sonnetdb_kv_entry_version");
    RESOLVE_SYMBOL(p_sonnetdb_kv_entry_expires_at_unix_ms, sonnetdb_kv_entry_expires_at_unix_ms_fn, "sonnetdb_kv_entry_expires_at_unix_ms");
    RESOLVE_SYMBOL(p_sonnetdb_kv_scan_next, sonnetdb_kv_scan_next_fn, "sonnetdb_kv_scan_next");
    RESOLVE_SYMBOL(p_sonnetdb_kv_scan_key, sonnetdb_kv_scan_key_fn, "sonnetdb_kv_scan_key");
    RESOLVE_SYMBOL(p_sonnetdb_kv_scan_value_length, sonnetdb_kv_scan_value_length_fn, "sonnetdb_kv_scan_value_length");
    RESOLVE_SYMBOL(p_sonnetdb_kv_scan_copy_value, sonnetdb_kv_scan_copy_value_fn, "sonnetdb_kv_scan_copy_value");
    RESOLVE_SYMBOL(p_sonnetdb_kv_scan_version, sonnetdb_kv_scan_version_fn, "sonnetdb_kv_scan_version");
    RESOLVE_SYMBOL(p_sonnetdb_kv_scan_expires_at_unix_ms, sonnetdb_kv_scan_expires_at_unix_ms_fn, "sonnetdb_kv_scan_expires_at_unix_ms");
    RESOLVE_SYMBOL(p_sonnetdb_kv_scan_free, sonnetdb_kv_scan_free_fn, "sonnetdb_kv_scan_free");
    RESOLVE_SYMBOL(p_sonnetdb_doc_open, sonnetdb_doc_open_fn, "sonnetdb_doc_open");
    RESOLVE_SYMBOL(p_sonnetdb_doc_close, sonnetdb_doc_close_fn, "sonnetdb_doc_close");
    RESOLVE_SYMBOL(p_sonnetdb_doc_create_collection, sonnetdb_doc_create_collection_fn, "sonnetdb_doc_create_collection");
    RESOLVE_SYMBOL(p_sonnetdb_doc_drop_collection, sonnetdb_doc_drop_collection_fn, "sonnetdb_doc_drop_collection");
    RESOLVE_SYMBOL(p_sonnetdb_doc_insert, sonnetdb_doc_json_fn, "sonnetdb_doc_insert");
    RESOLVE_SYMBOL(p_sonnetdb_doc_update, sonnetdb_doc_json_fn, "sonnetdb_doc_update");
    RESOLVE_SYMBOL(p_sonnetdb_doc_delete, sonnetdb_doc_json_fn, "sonnetdb_doc_delete");
    RESOLVE_SYMBOL(p_sonnetdb_doc_find_page, sonnetdb_doc_json_fn, "sonnetdb_doc_find_page");
    RESOLVE_SYMBOL(p_sonnetdb_doc_aggregate, sonnetdb_doc_json_fn, "sonnetdb_doc_aggregate");
    RESOLVE_SYMBOL(p_sonnetdb_doc_result_free, sonnetdb_doc_result_free_fn, "sonnetdb_doc_result_free");
    RESOLVE_SYMBOL(p_sonnetdb_doc_result_json_length, sonnetdb_doc_result_json_length_fn, "sonnetdb_doc_result_json_length");
    RESOLVE_SYMBOL(p_sonnetdb_doc_result_copy_json, sonnetdb_doc_result_copy_json_fn, "sonnetdb_doc_result_copy_json");
    RESOLVE_SYMBOL(p_sonnetdb_flush, sonnetdb_flush_fn, "sonnetdb_flush");
    RESOLVE_SYMBOL(p_sonnetdb_version, sonnetdb_version_fn, "sonnetdb_version");
    RESOLVE_SYMBOL(p_sonnetdb_last_error, sonnetdb_last_error_fn, "sonnetdb_last_error");
}

JNIEXPORT jlong JNICALL Java_com_sonnetdb_jni_SonnetDbJni_open(
    JNIEnv* env,
    jclass cls,
    jstring dataSource)
{
    (void)cls;
    const char* chars = (*env)->GetStringUTFChars(env, dataSource, NULL);
    if (chars == NULL)
    {
        return 0;
    }

    void* connection = p_sonnetdb_open(chars);
    (*env)->ReleaseStringUTFChars(env, dataSource, chars);
    if (connection == NULL)
    {
        throw_last_error(env, "sonnetdb_open failed.");
        return 0;
    }
    return (jlong)(intptr_t)connection;
}

JNIEXPORT void JNICALL Java_com_sonnetdb_jni_SonnetDbJni_close(
    JNIEnv* env,
    jclass cls,
    jlong connection)
{
    (void)env;
    (void)cls;
    if (connection != 0)
    {
        p_sonnetdb_close((void*)(intptr_t)connection);
    }
}

JNIEXPORT jlong JNICALL Java_com_sonnetdb_jni_SonnetDbJni_execute(
    JNIEnv* env,
    jclass cls,
    jlong connection,
    jstring sql)
{
    (void)cls;
    const char* chars = (*env)->GetStringUTFChars(env, sql, NULL);
    if (chars == NULL)
    {
        return 0;
    }

    void* result = p_sonnetdb_execute((void*)(intptr_t)connection, chars);
    (*env)->ReleaseStringUTFChars(env, sql, chars);
    if (result == NULL)
    {
        throw_last_error(env, "sonnetdb_execute failed.");
        return 0;
    }
    return (jlong)(intptr_t)result;
}

JNIEXPORT jint JNICALL Java_com_sonnetdb_jni_SonnetDbJni_bulkExecute(
    JNIEnv* env,
    jclass cls,
    jlong connection,
    jstring payload,
    jstring measurement,
    jstring onError,
    jstring flush)
{
    (void)cls;
    const char* payload_chars = (*env)->GetStringUTFChars(env, payload, NULL);
    if (payload_chars == NULL)
    {
        return -1;
    }

    void* bulk = p_sonnetdb_bulk_create(payload_chars);
    (*env)->ReleaseStringUTFChars(env, payload, payload_chars);
    if (bulk == NULL)
    {
        throw_last_error(env, "sonnetdb_bulk_create failed.");
        return -1;
    }

    jstring options[3] = { measurement, onError, flush };
    int32_t (*setters[3])(void*, const char*) = {
        p_sonnetdb_bulk_set_measurement,
        p_sonnetdb_bulk_set_onerror,
        p_sonnetdb_bulk_set_flush
    };
    const char* errors[3] = {
        "sonnetdb_bulk_set_measurement failed.",
        "sonnetdb_bulk_set_onerror failed.",
        "sonnetdb_bulk_set_flush failed."
    };
    for (int i = 0; i < 3; i++)
    {
        if (options[i] == NULL)
        {
            continue;
        }

        const char* value = (*env)->GetStringUTFChars(env, options[i], NULL);
        if (value == NULL)
        {
            p_sonnetdb_bulk_free(bulk);
            return -1;
        }
        if (value[0] != '\0' && setters[i](bulk, value) != 0)
        {
            (*env)->ReleaseStringUTFChars(env, options[i], value);
            p_sonnetdb_bulk_free(bulk);
            throw_last_error(env, errors[i]);
            return -1;
        }
        (*env)->ReleaseStringUTFChars(env, options[i], value);
    }

    void* result = p_sonnetdb_bulk_execute((void*)(intptr_t)connection, bulk);
    p_sonnetdb_bulk_free(bulk);
    if (result == NULL)
    {
        throw_last_error(env, "sonnetdb_bulk_execute failed.");
        return -1;
    }

    int32_t affected = p_sonnetdb_result_records_affected(result);
    p_sonnetdb_result_free(result);
    return (jint)affected;
}

JNIEXPORT void JNICALL Java_com_sonnetdb_jni_SonnetDbJni_resultFree(
    JNIEnv* env,
    jclass cls,
    jlong result)
{
    (void)env;
    (void)cls;
    if (result != 0)
    {
        p_sonnetdb_result_free((void*)(intptr_t)result);
    }
}

JNIEXPORT jint JNICALL Java_com_sonnetdb_jni_SonnetDbJni_recordsAffected(JNIEnv* env, jclass cls, jlong result)
{
    (void)env;
    (void)cls;
    return p_sonnetdb_result_records_affected((void*)(intptr_t)result);
}

JNIEXPORT jint JNICALL Java_com_sonnetdb_jni_SonnetDbJni_columnCount(JNIEnv* env, jclass cls, jlong result)
{
    (void)cls;
    int32_t value = p_sonnetdb_result_column_count((void*)(intptr_t)result);
    if (value < 0)
    {
        throw_last_error(env, "sonnetdb_result_column_count failed.");
    }
    return value;
}

JNIEXPORT jstring JNICALL Java_com_sonnetdb_jni_SonnetDbJni_columnName(JNIEnv* env, jclass cls, jlong result, jint ordinal)
{
    (void)cls;
    const char* value = p_sonnetdb_result_column_name((void*)(intptr_t)result, ordinal);
    if (value == NULL)
    {
        throw_last_error(env, "sonnetdb_result_column_name failed.");
        return NULL;
    }
    return (*env)->NewStringUTF(env, value);
}

JNIEXPORT jboolean JNICALL Java_com_sonnetdb_jni_SonnetDbJni_next(JNIEnv* env, jclass cls, jlong result)
{
    (void)cls;
    int32_t value = p_sonnetdb_result_next((void*)(intptr_t)result);
    if (value < 0)
    {
        throw_last_error(env, "sonnetdb_result_next failed.");
        return JNI_FALSE;
    }
    return value == 1 ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jint JNICALL Java_com_sonnetdb_jni_SonnetDbJni_valueType(JNIEnv* env, jclass cls, jlong result, jint ordinal)
{
    (void)cls;
    int32_t value = p_sonnetdb_result_value_type((void*)(intptr_t)result, ordinal);
    if (value < 0)
    {
        throw_last_error(env, "sonnetdb_result_value_type failed.");
    }
    return value;
}

JNIEXPORT jlong JNICALL Java_com_sonnetdb_jni_SonnetDbJni_valueInt64(JNIEnv* env, jclass cls, jlong result, jint ordinal)
{
    (void)env;
    (void)cls;
    return (jlong)p_sonnetdb_result_value_int64((void*)(intptr_t)result, ordinal);
}

JNIEXPORT jdouble JNICALL Java_com_sonnetdb_jni_SonnetDbJni_valueDouble(JNIEnv* env, jclass cls, jlong result, jint ordinal)
{
    (void)env;
    (void)cls;
    return (jdouble)p_sonnetdb_result_value_double((void*)(intptr_t)result, ordinal);
}

JNIEXPORT jboolean JNICALL Java_com_sonnetdb_jni_SonnetDbJni_valueBool(JNIEnv* env, jclass cls, jlong result, jint ordinal)
{
    (void)cls;
    int32_t value = p_sonnetdb_result_value_bool((void*)(intptr_t)result, ordinal);
    if (value < 0)
    {
        throw_last_error(env, "sonnetdb_result_value_bool failed.");
        return JNI_FALSE;
    }
    return value == 0 ? JNI_FALSE : JNI_TRUE;
}

JNIEXPORT jstring JNICALL Java_com_sonnetdb_jni_SonnetDbJni_valueText(JNIEnv* env, jclass cls, jlong result, jint ordinal)
{
    (void)cls;
    const char* value = p_sonnetdb_result_value_text((void*)(intptr_t)result, ordinal);
    if (value == NULL)
    {
        return NULL;
    }
    return (*env)->NewStringUTF(env, value);
}

JNIEXPORT jlong JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvOpen(
    JNIEnv* env,
    jclass cls,
    jlong connection,
    jstring keyspace,
    jstring namespaceName)
{
    (void)cls;
    const char* keyspace_chars = (*env)->GetStringUTFChars(env, keyspace, NULL);
    if (keyspace_chars == NULL)
    {
        return 0;
    }

    const char* namespace_chars = NULL;
    if (namespaceName != NULL)
    {
        namespace_chars = (*env)->GetStringUTFChars(env, namespaceName, NULL);
        if (namespace_chars == NULL)
        {
            (*env)->ReleaseStringUTFChars(env, keyspace, keyspace_chars);
            return 0;
        }
    }

    void* kv = p_sonnetdb_kv_open((void*)(intptr_t)connection, keyspace_chars, namespace_chars);
    if (namespace_chars != NULL)
    {
        (*env)->ReleaseStringUTFChars(env, namespaceName, namespace_chars);
    }
    (*env)->ReleaseStringUTFChars(env, keyspace, keyspace_chars);

    if (kv == NULL)
    {
        throw_last_error(env, "sonnetdb_kv_open failed.");
        return 0;
    }
    return (jlong)(intptr_t)kv;
}

JNIEXPORT void JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvClose(JNIEnv* env, jclass cls, jlong kv)
{
    (void)env;
    (void)cls;
    if (kv != 0)
    {
        p_sonnetdb_kv_close((void*)(intptr_t)kv);
    }
}

JNIEXPORT jlong JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvGet(
    JNIEnv* env,
    jclass cls,
    jlong kv,
    jstring key)
{
    (void)cls;
    const char* chars = (*env)->GetStringUTFChars(env, key, NULL);
    if (chars == NULL)
    {
        return 0;
    }

    void* entry = p_sonnetdb_kv_get((void*)(intptr_t)kv, chars);
    (*env)->ReleaseStringUTFChars(env, key, chars);
    if (entry == NULL)
    {
        char buffer[4096];
        buffer[0] = '\0';
        p_sonnetdb_last_error(buffer, (int32_t)sizeof(buffer));
        if (buffer[0] != '\0')
        {
            throw_sonnet(env, buffer);
        }
        return 0;
    }
    return (jlong)(intptr_t)entry;
}

JNIEXPORT jlong JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvSet(
    JNIEnv* env,
    jclass cls,
    jlong kv,
    jstring key,
    jbyteArray value,
    jlong expiresAtUnixMs)
{
    (void)cls;
    const char* chars = (*env)->GetStringUTFChars(env, key, NULL);
    if (chars == NULL)
    {
        return -1;
    }

    jbyte* bytes;
    jsize length;
    void* value_ptr = byte_array_elements(env, value, &bytes, &length);
    if ((*env)->ExceptionCheck(env))
    {
        (*env)->ReleaseStringUTFChars(env, key, chars);
        return -1;
    }

    int64_t version = p_sonnetdb_kv_set((void*)(intptr_t)kv, chars, value_ptr, (int32_t)length, expiresAtUnixMs);
    if (bytes != NULL)
    {
        (*env)->ReleaseByteArrayElements(env, value, bytes, JNI_ABORT);
    }
    (*env)->ReleaseStringUTFChars(env, key, chars);
    if (version < 0)
    {
        throw_last_error(env, "sonnetdb_kv_set failed.");
    }
    return (jlong)version;
}

JNIEXPORT jboolean JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvDelete(JNIEnv* env, jclass cls, jlong kv, jstring key)
{
    (void)cls;
    const char* chars = (*env)->GetStringUTFChars(env, key, NULL);
    if (chars == NULL)
    {
        return JNI_FALSE;
    }

    int32_t code = p_sonnetdb_kv_delete((void*)(intptr_t)kv, chars);
    (*env)->ReleaseStringUTFChars(env, key, chars);
    if (code < 0)
    {
        throw_last_error(env, "sonnetdb_kv_delete failed.");
        return JNI_FALSE;
    }
    return code == 1 ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jlong JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvScanPrefix(
    JNIEnv* env,
    jclass cls,
    jlong kv,
    jstring prefix,
    jint limit)
{
    (void)cls;
    const char* chars = (*env)->GetStringUTFChars(env, prefix, NULL);
    if (chars == NULL)
    {
        return 0;
    }

    void* scan = p_sonnetdb_kv_scan_prefix((void*)(intptr_t)kv, chars, limit);
    (*env)->ReleaseStringUTFChars(env, prefix, chars);
    if (scan == NULL)
    {
        throw_last_error(env, "sonnetdb_kv_scan_prefix failed.");
        return 0;
    }
    return (jlong)(intptr_t)scan;
}

JNIEXPORT jlong JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvTtl(
    JNIEnv* env,
    jclass cls,
    jlong kv,
    jstring key,
    jlongArray expiresAtUnixMs)
{
    (void)cls;
    const char* chars = (*env)->GetStringUTFChars(env, key, NULL);
    if (chars == NULL)
    {
        return -3;
    }

    int64_t expires = -1;
    int64_t ttl = p_sonnetdb_kv_ttl((void*)(intptr_t)kv, chars, &expires);
    (*env)->ReleaseStringUTFChars(env, key, chars);
    if (ttl < -2)
    {
        throw_last_error(env, "sonnetdb_kv_ttl failed.");
        return -3;
    }
    if (expiresAtUnixMs != NULL && (*env)->GetArrayLength(env, expiresAtUnixMs) > 0)
    {
        jlong out = (jlong)expires;
        (*env)->SetLongArrayRegion(env, expiresAtUnixMs, 0, 1, &out);
    }
    return (jlong)ttl;
}

JNIEXPORT jboolean JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvExpireAt(
    JNIEnv* env,
    jclass cls,
    jlong kv,
    jstring key,
    jlong expiresAtUnixMs)
{
    (void)cls;
    const char* chars = (*env)->GetStringUTFChars(env, key, NULL);
    if (chars == NULL)
    {
        return JNI_FALSE;
    }
    int32_t code = p_sonnetdb_kv_expire_at((void*)(intptr_t)kv, chars, expiresAtUnixMs);
    (*env)->ReleaseStringUTFChars(env, key, chars);
    if (code < 0)
    {
        throw_last_error(env, "sonnetdb_kv_expire_at failed.");
        return JNI_FALSE;
    }
    return code == 1 ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvPersist(JNIEnv* env, jclass cls, jlong kv, jstring key)
{
    (void)cls;
    const char* chars = (*env)->GetStringUTFChars(env, key, NULL);
    if (chars == NULL)
    {
        return JNI_FALSE;
    }
    int32_t code = p_sonnetdb_kv_persist((void*)(intptr_t)kv, chars);
    (*env)->ReleaseStringUTFChars(env, key, chars);
    if (code < 0)
    {
        throw_last_error(env, "sonnetdb_kv_persist failed.");
        return JNI_FALSE;
    }
    return code == 1 ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvIncr(
    JNIEnv* env,
    jclass cls,
    jlong kv,
    jstring key,
    jlong delta,
    jlongArray valueAndVersion)
{
    (void)cls;
    const char* chars = (*env)->GetStringUTFChars(env, key, NULL);
    if (chars == NULL)
    {
        return;
    }
    int64_t value = 0;
    int64_t version = 0;
    int32_t code = p_sonnetdb_kv_incr((void*)(intptr_t)kv, chars, delta, &value, &version);
    (*env)->ReleaseStringUTFChars(env, key, chars);
    if (code != 0)
    {
        throw_last_error(env, "sonnetdb_kv_incr failed.");
        return;
    }
    if (valueAndVersion != NULL && (*env)->GetArrayLength(env, valueAndVersion) >= 2)
    {
        jlong out[2] = { (jlong)value, (jlong)version };
        (*env)->SetLongArrayRegion(env, valueAndVersion, 0, 2, out);
    }
}

JNIEXPORT jboolean JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvCas(
    JNIEnv* env,
    jclass cls,
    jlong kv,
    jstring key,
    jlong expectedVersion,
    jbyteArray value,
    jlong expiresAtUnixMs,
    jlongArray currentAndNewVersion)
{
    (void)cls;
    const char* chars = (*env)->GetStringUTFChars(env, key, NULL);
    if (chars == NULL)
    {
        return JNI_FALSE;
    }
    jbyte* bytes;
    jsize length;
    void* value_ptr = byte_array_elements(env, value, &bytes, &length);
    if ((*env)->ExceptionCheck(env))
    {
        (*env)->ReleaseStringUTFChars(env, key, chars);
        return JNI_FALSE;
    }

    int64_t current = 0;
    int64_t next = -1;
    int32_t code = p_sonnetdb_kv_cas(
        (void*)(intptr_t)kv,
        chars,
        expectedVersion,
        value_ptr,
        (int32_t)length,
        expiresAtUnixMs,
        &current,
        &next);
    if (bytes != NULL)
    {
        (*env)->ReleaseByteArrayElements(env, value, bytes, JNI_ABORT);
    }
    (*env)->ReleaseStringUTFChars(env, key, chars);
    if (code < 0)
    {
        throw_last_error(env, "sonnetdb_kv_cas failed.");
        return JNI_FALSE;
    }
    if (currentAndNewVersion != NULL && (*env)->GetArrayLength(env, currentAndNewVersion) >= 2)
    {
        jlong out[2] = { (jlong)current, (jlong)next };
        (*env)->SetLongArrayRegion(env, currentAndNewVersion, 0, 2, out);
    }
    return code == 1 ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvEntryFree(JNIEnv* env, jclass cls, jlong entry)
{
    (void)env;
    (void)cls;
    if (entry != 0)
    {
        p_sonnetdb_kv_entry_free((void*)(intptr_t)entry);
    }
}

JNIEXPORT jstring JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvEntryKey(JNIEnv* env, jclass cls, jlong entry)
{
    (void)cls;
    const char* key = p_sonnetdb_kv_entry_key((void*)(intptr_t)entry);
    if (key == NULL)
    {
        throw_last_error(env, "sonnetdb_kv_entry_key failed.");
        return NULL;
    }
    return (*env)->NewStringUTF(env, key);
}

JNIEXPORT jbyteArray JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvEntryValue(JNIEnv* env, jclass cls, jlong entry)
{
    (void)cls;
    return copy_bytes(env, (void*)(intptr_t)entry, p_sonnetdb_kv_entry_value_length, p_sonnetdb_kv_entry_copy_value);
}

JNIEXPORT jlong JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvEntryVersion(JNIEnv* env, jclass cls, jlong entry)
{
    (void)env;
    (void)cls;
    return (jlong)p_sonnetdb_kv_entry_version((void*)(intptr_t)entry);
}

JNIEXPORT jlong JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvEntryExpiresAtUnixMs(JNIEnv* env, jclass cls, jlong entry)
{
    (void)env;
    (void)cls;
    return (jlong)p_sonnetdb_kv_entry_expires_at_unix_ms((void*)(intptr_t)entry);
}

JNIEXPORT jboolean JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvScanNext(JNIEnv* env, jclass cls, jlong scan)
{
    (void)cls;
    int32_t code = p_sonnetdb_kv_scan_next((void*)(intptr_t)scan);
    if (code < 0)
    {
        throw_last_error(env, "sonnetdb_kv_scan_next failed.");
        return JNI_FALSE;
    }
    return code == 1 ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jstring JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvScanKey(JNIEnv* env, jclass cls, jlong scan)
{
    (void)cls;
    const char* key = p_sonnetdb_kv_scan_key((void*)(intptr_t)scan);
    if (key == NULL)
    {
        throw_last_error(env, "sonnetdb_kv_scan_key failed.");
        return NULL;
    }
    return (*env)->NewStringUTF(env, key);
}

JNIEXPORT jbyteArray JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvScanValue(JNIEnv* env, jclass cls, jlong scan)
{
    (void)cls;
    return copy_bytes(env, (void*)(intptr_t)scan, p_sonnetdb_kv_scan_value_length, p_sonnetdb_kv_scan_copy_value);
}

JNIEXPORT jlong JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvScanVersion(JNIEnv* env, jclass cls, jlong scan)
{
    (void)env;
    (void)cls;
    return (jlong)p_sonnetdb_kv_scan_version((void*)(intptr_t)scan);
}

JNIEXPORT jlong JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvScanExpiresAtUnixMs(JNIEnv* env, jclass cls, jlong scan)
{
    (void)env;
    (void)cls;
    return (jlong)p_sonnetdb_kv_scan_expires_at_unix_ms((void*)(intptr_t)scan);
}

JNIEXPORT void JNICALL Java_com_sonnetdb_jni_SonnetDbJni_kvScanFree(JNIEnv* env, jclass cls, jlong scan)
{
    (void)env;
    (void)cls;
    if (scan != 0)
    {
        p_sonnetdb_kv_scan_free((void*)(intptr_t)scan);
    }
}

JNIEXPORT jlong JNICALL Java_com_sonnetdb_jni_SonnetDbJni_docOpen(
    JNIEnv* env,
    jclass cls,
    jlong connection,
    jstring collection)
{
    (void)cls;
    const char* chars = (*env)->GetStringUTFChars(env, collection, NULL);
    if (chars == NULL)
    {
        return 0;
    }

    void* document = p_sonnetdb_doc_open((void*)(intptr_t)connection, chars);
    (*env)->ReleaseStringUTFChars(env, collection, chars);
    if (document == NULL)
    {
        throw_last_error(env, "sonnetdb_doc_open failed.");
        return 0;
    }
    return (jlong)(intptr_t)document;
}

JNIEXPORT void JNICALL Java_com_sonnetdb_jni_SonnetDbJni_docClose(JNIEnv* env, jclass cls, jlong document)
{
    (void)env;
    (void)cls;
    if (document != 0)
    {
        p_sonnetdb_doc_close((void*)(intptr_t)document);
    }
}

static jstring doc_json_call(JNIEnv* env, jlong document, jstring payloadJson, sonnetdb_doc_json_fn fn, const char* fallback)
{
    const char* chars = NULL;
    if (payloadJson != NULL)
    {
        chars = (*env)->GetStringUTFChars(env, payloadJson, NULL);
        if (chars == NULL)
        {
            return NULL;
        }
    }

    void* result = fn((void*)(intptr_t)document, chars);
    if (chars != NULL)
    {
        (*env)->ReleaseStringUTFChars(env, payloadJson, chars);
    }
    if (result == NULL)
    {
        throw_last_error(env, fallback);
        return NULL;
    }

    jstring json = copy_doc_json(env, result, fallback);
    p_sonnetdb_doc_result_free(result);
    return json;
}

JNIEXPORT jstring JNICALL Java_com_sonnetdb_jni_SonnetDbJni_docCreateCollection(
    JNIEnv* env,
    jclass cls,
    jlong document,
    jstring optionsJson)
{
    (void)cls;
    return doc_json_call(env, document, optionsJson, p_sonnetdb_doc_create_collection, "sonnetdb_doc_create_collection failed.");
}

JNIEXPORT jboolean JNICALL Java_com_sonnetdb_jni_SonnetDbJni_docDropCollection(JNIEnv* env, jclass cls, jlong document)
{
    (void)cls;
    int32_t code = p_sonnetdb_doc_drop_collection((void*)(intptr_t)document);
    if (code < 0)
    {
        throw_last_error(env, "sonnetdb_doc_drop_collection failed.");
        return JNI_FALSE;
    }
    return code == 1 ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jstring JNICALL Java_com_sonnetdb_jni_SonnetDbJni_docInsert(
    JNIEnv* env,
    jclass cls,
    jlong document,
    jstring payloadJson)
{
    (void)cls;
    return doc_json_call(env, document, payloadJson, p_sonnetdb_doc_insert, "sonnetdb_doc_insert failed.");
}

JNIEXPORT jstring JNICALL Java_com_sonnetdb_jni_SonnetDbJni_docUpdate(
    JNIEnv* env,
    jclass cls,
    jlong document,
    jstring payloadJson)
{
    (void)cls;
    return doc_json_call(env, document, payloadJson, p_sonnetdb_doc_update, "sonnetdb_doc_update failed.");
}

JNIEXPORT jstring JNICALL Java_com_sonnetdb_jni_SonnetDbJni_docDelete(
    JNIEnv* env,
    jclass cls,
    jlong document,
    jstring payloadJson)
{
    (void)cls;
    return doc_json_call(env, document, payloadJson, p_sonnetdb_doc_delete, "sonnetdb_doc_delete failed.");
}

JNIEXPORT jstring JNICALL Java_com_sonnetdb_jni_SonnetDbJni_docFindPage(
    JNIEnv* env,
    jclass cls,
    jlong document,
    jstring payloadJson)
{
    (void)cls;
    return doc_json_call(env, document, payloadJson, p_sonnetdb_doc_find_page, "sonnetdb_doc_find_page failed.");
}

JNIEXPORT jstring JNICALL Java_com_sonnetdb_jni_SonnetDbJni_docAggregate(
    JNIEnv* env,
    jclass cls,
    jlong document,
    jstring payloadJson)
{
    (void)cls;
    return doc_json_call(env, document, payloadJson, p_sonnetdb_doc_aggregate, "sonnetdb_doc_aggregate failed.");
}

JNIEXPORT void JNICALL Java_com_sonnetdb_jni_SonnetDbJni_flush(JNIEnv* env, jclass cls, jlong connection)
{
    (void)cls;
    int32_t value = p_sonnetdb_flush((void*)(intptr_t)connection);
    if (value != 0)
    {
        throw_last_error(env, "sonnetdb_flush failed.");
    }
}

static jstring copy_string(JNIEnv* env, int32_t (*fn)(char*, int32_t))
{
    char stack_buffer[4096];
    int32_t required = fn(stack_buffer, (int32_t)sizeof(stack_buffer));
    if (required < 0)
    {
        return NULL;
    }
    if (required < (int32_t)sizeof(stack_buffer))
    {
        return (*env)->NewStringUTF(env, stack_buffer);
    }

    char* heap_buffer = (char*)malloc((size_t)required + 1);
    if (heap_buffer == NULL)
    {
        throw_sonnet(env, "Out of memory.");
        return NULL;
    }
    required = fn(heap_buffer, required + 1);
    if (required < 0)
    {
        free(heap_buffer);
        return NULL;
    }
    jstring result = (*env)->NewStringUTF(env, heap_buffer);
    free(heap_buffer);
    return result;
}

JNIEXPORT jstring JNICALL Java_com_sonnetdb_jni_SonnetDbJni_version(JNIEnv* env, jclass cls)
{
    (void)cls;
    return copy_string(env, p_sonnetdb_version);
}

JNIEXPORT jstring JNICALL Java_com_sonnetdb_jni_SonnetDbJni_lastError(JNIEnv* env, jclass cls)
{
    (void)cls;
    return copy_string(env, p_sonnetdb_last_error);
}
