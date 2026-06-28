#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#ifdef _WIN32
#include <process.h>
#define SONNETDB_PATH_SEPARATOR "\\"
#define SONNETDB_GETPID _getpid
#else
#include <unistd.h>
#define SONNETDB_PATH_SEPARATOR "/"
#define SONNETDB_GETPID getpid
#endif

#include "sonnetdb.h"

static void print_last_error(void)
{
    char buffer[1024];
    int32_t written = sonnetdb_last_error(buffer, (int32_t)sizeof(buffer));
    if (written > 0)
    {
        fprintf(stderr, "SonnetDB error: %s\n", buffer);
    }
}

static void require_result(sonnetdb_result* result)
{
    if (result == NULL)
    {
        print_last_error();
        exit(1);
    }
}

static void require_ok(int32_t rc)
{
    if (rc != 0)
    {
        print_last_error();
        exit(1);
    }
}

static void require_entry(sonnetdb_kv_entry* entry)
{
    if (entry == NULL)
    {
        print_last_error();
        exit(1);
    }
}

static void copy_kv_value(sonnetdb_kv_entry* entry, char* buffer, size_t buffer_length)
{
    int32_t required = sonnetdb_kv_entry_copy_value(entry, buffer, (int32_t)(buffer_length - 1));
    if (required < 0)
    {
        print_last_error();
        exit(1);
    }

    size_t end = (size_t)required < buffer_length - 1 ? (size_t)required : buffer_length - 1;
    buffer[end] = '\0';
}

int main(void)
{
#ifdef _WIN32
    const char* temp_root = getenv("TEMP");
    if (temp_root == NULL || temp_root[0] == '\0')
    {
        temp_root = getenv("TMP");
    }
#else
    const char* temp_root = getenv("TMPDIR");
#endif
    if (temp_root == NULL || temp_root[0] == '\0')
    {
        temp_root = ".";
    }

    char data_source[512];
    snprintf(
        data_source,
        sizeof(data_source),
        "%s%s%s-%ld-%d",
        temp_root,
        SONNETDB_PATH_SEPARATOR,
        "sonnetdb-c-quickstart",
        (long)time(NULL),
        (int)SONNETDB_GETPID());

    sonnetdb_connection* connection = sonnetdb_open(data_source);
    if (connection == NULL)
    {
        print_last_error();
        return 1;
    }

    sonnetdb_result* result = sonnetdb_execute(
        connection,
        "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
    require_result(result);
    sonnetdb_result_free(result);

    result = sonnetdb_execute(
        connection,
        "INSERT INTO cpu (time, host, usage) VALUES "
        "(1710000000000, 'edge-1', 0.42),"
        "(1710000001000, 'edge-1', 0.73)");
    require_result(result);
    printf("inserted rows: %d\n", sonnetdb_result_records_affected(result));
    sonnetdb_result_free(result);

    sonnetdb_bulk* bulk = sonnetdb_bulk_create(
        "ignored,host=edge-2 usage=0.81 1710000002000\n"
        "ignored,host=edge-2 usage=0.86 1710000003000");
    if (bulk == NULL)
    {
        print_last_error();
        sonnetdb_close(connection);
        return 1;
    }

    require_ok(sonnetdb_bulk_set_measurement(bulk, "cpu"));
    require_ok(sonnetdb_bulk_set_onerror(bulk, "failfast"));
    require_ok(sonnetdb_bulk_set_flush(bulk, "false"));
    result = sonnetdb_bulk_execute(connection, bulk);
    sonnetdb_bulk_free(bulk);
    require_result(result);
    printf("bulk rows: %d\n", sonnetdb_result_records_affected(result));
    sonnetdb_result_free(result);

    sonnetdb_kv* kv = sonnetdb_kv_open(connection, "app-cache", "quickstart");
    if (kv == NULL)
    {
        print_last_error();
        sonnetdb_close(connection);
        return 1;
    }

    const char* kv_value = "online";
    int64_t kv_version = sonnetdb_kv_set(
        kv,
        "device:edge-1",
        kv_value,
        (int32_t)strlen(kv_value),
        -1);
    if (kv_version < 0)
    {
        print_last_error();
        sonnetdb_kv_close(kv);
        sonnetdb_close(connection);
        return 1;
    }

    sonnetdb_kv_entry* entry = sonnetdb_kv_get(kv, "device:edge-1");
    require_entry(entry);
    char value_buffer[128];
    copy_kv_value(entry, value_buffer, sizeof(value_buffer));
    printf("kv %s = %s (version %lld)\n",
           sonnetdb_kv_entry_key(entry),
           value_buffer,
           (long long)sonnetdb_kv_entry_version(entry));
    int64_t cas_base_version = sonnetdb_kv_entry_version(entry);
    sonnetdb_kv_entry_free(entry);

    int32_t expired = sonnetdb_kv_expire_at(kv, "device:edge-1", 4102444800000LL);
    if (expired < 0)
    {
        print_last_error();
        sonnetdb_kv_close(kv);
        sonnetdb_close(connection);
        return 1;
    }

    int64_t expires_at = -1;
    int64_t ttl_ms = sonnetdb_kv_ttl(kv, "device:edge-1", &expires_at);
    if (ttl_ms < -2)
    {
        print_last_error();
        sonnetdb_kv_close(kv);
        sonnetdb_close(connection);
        return 1;
    }
    printf("kv ttl: %lld ms (expires at %lld)\n",
           (long long)ttl_ms,
           (long long)expires_at);

    entry = sonnetdb_kv_get(kv, "device:edge-1");
    require_entry(entry);
    cas_base_version = sonnetdb_kv_entry_version(entry);
    sonnetdb_kv_entry_free(entry);

    int64_t counter_value = 0;
    int64_t counter_version = 0;
    require_ok(sonnetdb_kv_incr(kv, "counter", 3, &counter_value, &counter_version));
    printf("kv counter: %lld (version %lld)\n",
           (long long)counter_value,
           (long long)counter_version);

    int64_t current_version = 0;
    int64_t new_version = 0;
    const char* next_value = "offline";
    int32_t swapped = sonnetdb_kv_cas(
        kv,
        "device:edge-1",
        cas_base_version,
        next_value,
        (int32_t)strlen(next_value),
        -1,
        &current_version,
        &new_version);
    if (swapped < 0)
    {
        print_last_error();
        sonnetdb_kv_close(kv);
        sonnetdb_close(connection);
        return 1;
    }
    printf("kv cas swapped: %d (current %lld, new %lld)\n",
           swapped,
           (long long)current_version,
           (long long)new_version);

    sonnetdb_kv_scan* scan = sonnetdb_kv_scan_prefix(kv, "device:", 10);
    if (scan == NULL)
    {
        print_last_error();
        sonnetdb_kv_close(kv);
        sonnetdb_close(connection);
        return 1;
    }

    int32_t next = 0;
    while ((next = sonnetdb_kv_scan_next(scan)) == 1)
    {
        int32_t copied = sonnetdb_kv_scan_copy_value(scan, value_buffer, (int32_t)(sizeof(value_buffer) - 1));
        if (copied < 0)
        {
            print_last_error();
            sonnetdb_kv_scan_free(scan);
            sonnetdb_kv_close(kv);
            sonnetdb_close(connection);
            return 1;
        }
        size_t end = (size_t)copied < sizeof(value_buffer) - 1 ? (size_t)copied : sizeof(value_buffer) - 1;
        value_buffer[end] = '\0';
        printf("kv scan %s = %s\n", sonnetdb_kv_scan_key(scan), value_buffer);
    }
    if (next < 0)
    {
        print_last_error();
        sonnetdb_kv_scan_free(scan);
        sonnetdb_kv_close(kv);
        sonnetdb_close(connection);
        return 1;
    }
    sonnetdb_kv_scan_free(scan);

    if (sonnetdb_kv_delete(kv, "device:edge-1") < 0)
    {
        print_last_error();
        sonnetdb_kv_close(kv);
        sonnetdb_close(connection);
        return 1;
    }
    sonnetdb_kv_close(kv);

    result = sonnetdb_execute(
        connection,
        "SELECT time, host, usage FROM cpu WHERE host = 'edge-1' LIMIT 10");
    require_result(result);

    int32_t columns = sonnetdb_result_column_count(result);
    for (int32_t i = 0; i < columns; i++)
    {
        printf("%s%s", i == 0 ? "" : "\t", sonnetdb_result_column_name(result, i));
    }
    printf("\n");

    while (sonnetdb_result_next(result) == 1)
    {
        printf("%lld\t%s\t%.3f\n",
               (long long)sonnetdb_result_value_int64(result, 0),
               sonnetdb_result_value_text(result, 1),
               sonnetdb_result_value_double(result, 2));
    }

    sonnetdb_result_free(result);
    sonnetdb_close(connection);
    return 0;
}
