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

static void require_doc_result(sonnetdb_doc_result* result)
{
    if (result == NULL)
    {
        print_last_error();
        exit(1);
    }
}

static void require_obj_result(sonnetdb_obj_result* result)
{
    if (result == NULL)
    {
        print_last_error();
        exit(1);
    }
}

static void require_obj_writer(sonnetdb_obj_writer* writer)
{
    if (writer == NULL)
    {
        print_last_error();
        exit(1);
    }
}

static void require_obj_reader(sonnetdb_obj_reader* reader)
{
    if (reader == NULL)
    {
        print_last_error();
        exit(1);
    }
}

static void require_mq(sonnetdb_mq* queue)
{
    if (queue == NULL)
    {
        print_last_error();
        exit(1);
    }
}

static void require_mq_pull(sonnetdb_mq_pull_result* pull)
{
    if (pull == NULL)
    {
        print_last_error();
        exit(1);
    }
}

static void require_mq_result(sonnetdb_mq_result* result)
{
    if (result == NULL)
    {
        print_last_error();
        exit(1);
    }
}

static void print_doc_json(const char* label, sonnetdb_doc_result* result)
{
    int32_t required = sonnetdb_doc_result_json_length(result);
    if (required < 0)
    {
        print_last_error();
        exit(1);
    }

    char* buffer = (char*)malloc((size_t)required + 1);
    if (buffer == NULL)
    {
        fprintf(stderr, "out of memory\n");
        exit(1);
    }

    int32_t copied = sonnetdb_doc_result_copy_json(result, buffer, required + 1);
    if (copied < 0)
    {
        free(buffer);
        print_last_error();
        exit(1);
    }

    printf("%s: %s\n", label, buffer);
    free(buffer);
}

static char* copy_obj_json(sonnetdb_obj_result* result)
{
    int32_t required = sonnetdb_obj_result_json_length(result);
    if (required < 0)
    {
        print_last_error();
        exit(1);
    }

    char* buffer = (char*)malloc((size_t)required + 1);
    if (buffer == NULL)
    {
        fprintf(stderr, "out of memory\n");
        exit(1);
    }

    int32_t copied = sonnetdb_obj_result_copy_json(result, buffer, required + 1);
    if (copied < 0)
    {
        free(buffer);
        print_last_error();
        exit(1);
    }

    return buffer;
}

static void print_obj_json(const char* label, sonnetdb_obj_result* result)
{
    char* json = copy_obj_json(result);
    printf("%s: %s\n", label, json);
    free(json);
}

static void print_mq_json(const char* label, sonnetdb_mq_result* result)
{
    int32_t required = sonnetdb_mq_result_json_length(result);
    if (required < 0)
    {
        print_last_error();
        exit(1);
    }

    char* buffer = (char*)malloc((size_t)required + 1);
    if (buffer == NULL)
    {
        fprintf(stderr, "out of memory\n");
        exit(1);
    }

    int32_t copied = sonnetdb_mq_result_copy_json(result, buffer, required + 1);
    if (copied < 0)
    {
        free(buffer);
        print_last_error();
        exit(1);
    }

    printf("%s: %s\n", label, buffer);
    free(buffer);
}

static void extract_json_string(const char* json, const char* property, char* buffer, size_t buffer_length)
{
    char pattern[64];
    snprintf(pattern, sizeof(pattern), "\"%s\":\"", property);
    const char* start = strstr(json, pattern);
    if (start == NULL)
    {
        fprintf(stderr, "missing JSON property: %s\n", property);
        exit(1);
    }

    start += strlen(pattern);
    const char* end = strchr(start, '"');
    if (end == NULL)
    {
        fprintf(stderr, "unterminated JSON property: %s\n", property);
        exit(1);
    }

    size_t length = (size_t)(end - start);
    if (length >= buffer_length)
    {
        fprintf(stderr, "JSON property is too long: %s\n", property);
        exit(1);
    }

    memcpy(buffer, start, length);
    buffer[length] = '\0';
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

    sonnetdb_doc* documents = sonnetdb_doc_open(connection, "devices");
    if (documents == NULL)
    {
        print_last_error();
        sonnetdb_close(connection);
        return 1;
    }

    sonnetdb_doc_result* doc_result = sonnetdb_doc_create_collection(documents, "{\"ifNotExists\":true}");
    require_doc_result(doc_result);
    print_doc_json("doc create", doc_result);
    sonnetdb_doc_result_free(doc_result);

    doc_result = sonnetdb_doc_insert(
        documents,
        "{\"documents\":["
        "{\"id\":\"dev-1\",\"document\":{\"site\":\"north\",\"kind\":\"pump\",\"score\":7}},"
        "{\"id\":\"dev-2\",\"document\":{\"site\":\"south\",\"kind\":\"fan\",\"score\":3}}"
        "],\"ordered\":true}");
    require_doc_result(doc_result);
    print_doc_json("doc insert", doc_result);
    sonnetdb_doc_result_free(doc_result);

    doc_result = sonnetdb_doc_find_page(
        documents,
        "{\"limit\":10,"
        "\"filter\":{\"path\":\"$.site\",\"op\":\"eq\",\"value\":\"north\"},"
        "\"projection\":[{\"name\":\"_id\",\"path\":\"_id\"},{\"name\":\"score\",\"path\":\"$.score\"}],"
        "\"sort\":[{\"path\":\"$.score\",\"descending\":true}]}");
    require_doc_result(doc_result);
    print_doc_json("doc find", doc_result);
    sonnetdb_doc_result_free(doc_result);

    doc_result = sonnetdb_doc_update(
        documents,
        "{\"id\":\"dev-1\",\"update\":{\"set\":{\"$.status\":\"ok\"},\"inc\":{\"$.score\":1}}}");
    require_doc_result(doc_result);
    print_doc_json("doc update", doc_result);
    sonnetdb_doc_result_free(doc_result);

    doc_result = sonnetdb_doc_aggregate(
        documents,
        "["
        "{\"$match\":{\"path\":\"$.site\",\"op\":\"eq\",\"value\":\"north\"}},"
        "{\"$group\":{\"keys\":[{\"name\":\"site\",\"path\":\"$.site\"}],"
        "\"accumulators\":[{\"name\":\"rows\",\"op\":\"count\"},{\"name\":\"total\",\"op\":\"sum\",\"path\":\"$.score\"}]}}"
        "]");
    require_doc_result(doc_result);
    print_doc_json("doc aggregate", doc_result);
    sonnetdb_doc_result_free(doc_result);

    doc_result = sonnetdb_doc_delete(documents, "{\"ids\":[\"dev-2\"],\"ordered\":true}");
    require_doc_result(doc_result);
    print_doc_json("doc delete", doc_result);
    sonnetdb_doc_result_free(doc_result);

    int32_t dropped = sonnetdb_doc_drop_collection(documents);
    if (dropped < 0)
    {
        print_last_error();
        sonnetdb_doc_close(documents);
        sonnetdb_close(connection);
        return 1;
    }
    printf("doc dropped: %d\n", dropped);
    sonnetdb_doc_close(documents);

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

    sonnetdb_obj* objects = sonnetdb_obj_open(connection, "artifacts");
    if (objects == NULL)
    {
        print_last_error();
        sonnetdb_close(connection);
        return 1;
    }

    sonnetdb_obj_result* obj_result = sonnetdb_obj_create_bucket(objects, "artifact");
    require_obj_result(obj_result);
    print_obj_json("object bucket", obj_result);
    sonnetdb_obj_result_free(obj_result);

    obj_result = sonnetdb_obj_list_buckets(objects);
    require_obj_result(obj_result);
    print_obj_json("object buckets", obj_result);
    sonnetdb_obj_result_free(obj_result);

    sonnetdb_obj_writer* writer = sonnetdb_obj_writer_create(
        "text/plain",
        "{\"source\":\"c-quickstart\"}",
        "{\"kind\":\"demo\"}");
    require_obj_writer(writer);
    const char* chunk1 = "hello ";
    const char* chunk2 = "object storage";
    require_ok(sonnetdb_obj_writer_write(writer, chunk1, (int32_t)strlen(chunk1)));
    require_ok(sonnetdb_obj_writer_write(writer, chunk2, (int32_t)strlen(chunk2)));
    obj_result = sonnetdb_obj_put(objects, "logs/hello.txt", writer);
    sonnetdb_obj_writer_free(writer);
    require_obj_result(obj_result);
    print_obj_json("object put", obj_result);
    sonnetdb_obj_result_free(obj_result);

    sonnetdb_obj_reader* reader = sonnetdb_obj_get(objects, "logs/hello.txt", 6, 6);
    require_obj_reader(reader);
    char object_buffer[64];
    int32_t object_read = sonnetdb_obj_reader_read(reader, object_buffer, (int32_t)(sizeof(object_buffer) - 1));
    if (object_read < 0)
    {
        print_last_error();
        sonnetdb_obj_reader_free(reader);
        sonnetdb_obj_close(objects);
        sonnetdb_close(connection);
        return 1;
    }
    object_buffer[object_read] = '\0';
    printf("object range %s/%s: %s (%lld bytes total)\n",
           sonnetdb_obj_reader_bucket(reader),
           sonnetdb_obj_reader_key(reader),
           object_buffer,
           (long long)sonnetdb_obj_reader_size_bytes(reader));
    sonnetdb_obj_reader_free(reader);

    obj_result = sonnetdb_obj_list(objects, "logs/", 10, NULL);
    require_obj_result(obj_result);
    print_obj_json("object list", obj_result);
    sonnetdb_obj_result_free(obj_result);

    obj_result = sonnetdb_obj_multipart_initiate(objects, "logs/multipart.txt", "text/plain", NULL, NULL);
    require_obj_result(obj_result);
    char* multipart_json = copy_obj_json(obj_result);
    char upload_id[160];
    extract_json_string(multipart_json, "uploadId", upload_id, sizeof(upload_id));
    printf("object multipart uploadId: %s\n", upload_id);
    free(multipart_json);
    sonnetdb_obj_result_free(obj_result);

    writer = sonnetdb_obj_writer_create("text/plain", NULL, NULL);
    require_obj_writer(writer);
    const char* part1 = "part-one ";
    require_ok(sonnetdb_obj_writer_write(writer, part1, (int32_t)strlen(part1)));
    obj_result = sonnetdb_obj_multipart_upload_part(objects, "logs/multipart.txt", upload_id, 1, writer);
    sonnetdb_obj_writer_free(writer);
    require_obj_result(obj_result);
    print_obj_json("object multipart part1", obj_result);
    sonnetdb_obj_result_free(obj_result);

    writer = sonnetdb_obj_writer_create("text/plain", NULL, NULL);
    require_obj_writer(writer);
    const char* part2 = "part-two";
    require_ok(sonnetdb_obj_writer_write(writer, part2, (int32_t)strlen(part2)));
    obj_result = sonnetdb_obj_multipart_upload_part(objects, "logs/multipart.txt", upload_id, 2, writer);
    sonnetdb_obj_writer_free(writer);
    require_obj_result(obj_result);
    print_obj_json("object multipart part2", obj_result);
    sonnetdb_obj_result_free(obj_result);

    obj_result = sonnetdb_obj_multipart_complete(objects, "logs/multipart.txt", upload_id, "[1,2]");
    require_obj_result(obj_result);
    print_obj_json("object multipart complete", obj_result);
    sonnetdb_obj_result_free(obj_result);

    obj_result = sonnetdb_obj_delete_many(objects, "[\"logs/hello.txt\",\"logs/multipart.txt\"]");
    require_obj_result(obj_result);
    print_obj_json("object delete many", obj_result);
    sonnetdb_obj_result_free(obj_result);
    sonnetdb_obj_close(objects);

    sonnetdb_mq* queue = sonnetdb_mq_open(connection, "events.demo");
    require_mq(queue);

    const char* message1 = "pump online";
    int64_t offset1 = sonnetdb_mq_publish(
        queue,
        message1,
        (int32_t)strlen(message1),
        "{\"source\":\"c-quickstart\",\"kind\":\"status\"}");
    if (offset1 < 0)
    {
        print_last_error();
        sonnetdb_mq_close(queue);
        sonnetdb_close(connection);
        return 1;
    }

    const char* message2 = "fan offline";
    int64_t offset2 = sonnetdb_mq_publish(
        queue,
        message2,
        (int32_t)strlen(message2),
        NULL);
    if (offset2 < 0)
    {
        print_last_error();
        sonnetdb_mq_close(queue);
        sonnetdb_close(connection);
        return 1;
    }
    printf("mq published offsets: %lld, %lld\n", (long long)offset1, (long long)offset2);

    sonnetdb_mq_pull_result* pull = sonnetdb_mq_pull(queue, "quickstart-consumer", 10);
    require_mq_pull(pull);
    printf("mq pulled messages: %d\n", sonnetdb_mq_pull_result_message_count(pull));

    int64_t last_offset = -1;
    while ((next = sonnetdb_mq_pull_next(pull)) == 1)
    {
        int64_t payload_length = sonnetdb_mq_pull_payload_length(pull);
        if (payload_length < 0 || payload_length >= (int64_t)sizeof(value_buffer))
        {
            fprintf(stderr, "unexpected mq payload length: %lld\n", (long long)payload_length);
            sonnetdb_mq_pull_result_free(pull);
            sonnetdb_mq_close(queue);
            sonnetdb_close(connection);
            return 1;
        }

        int32_t copied = sonnetdb_mq_pull_copy_payload(pull, value_buffer, (int32_t)sizeof(value_buffer));
        if (copied < 0)
        {
            print_last_error();
            sonnetdb_mq_pull_result_free(pull);
            sonnetdb_mq_close(queue);
            sonnetdb_close(connection);
            return 1;
        }
        value_buffer[payload_length] = '\0';

        int32_t headers_required = sonnetdb_mq_pull_headers_json_length(pull);
        if (headers_required < 0 || headers_required >= (int32_t)sizeof(object_buffer))
        {
            fprintf(stderr, "unexpected mq headers length: %d\n", headers_required);
            sonnetdb_mq_pull_result_free(pull);
            sonnetdb_mq_close(queue);
            sonnetdb_close(connection);
            return 1;
        }
        int32_t headers_copied = sonnetdb_mq_pull_copy_headers_json(pull, object_buffer, headers_required + 1);
        if (headers_copied < 0)
        {
            print_last_error();
            sonnetdb_mq_pull_result_free(pull);
            sonnetdb_mq_close(queue);
            sonnetdb_close(connection);
            return 1;
        }

        last_offset = sonnetdb_mq_pull_offset(pull);
        printf("mq %s[%lld] at %lld: %s headers=%s\n",
               sonnetdb_mq_pull_topic(pull),
               (long long)last_offset,
               (long long)sonnetdb_mq_pull_timestamp_unix_ms(pull),
               value_buffer,
               object_buffer);
    }
    if (next < 0)
    {
        print_last_error();
        sonnetdb_mq_pull_result_free(pull);
        sonnetdb_mq_close(queue);
        sonnetdb_close(connection);
        return 1;
    }
    sonnetdb_mq_pull_result_free(pull);

    int64_t next_offset = sonnetdb_mq_ack(queue, "quickstart-consumer", last_offset);
    if (next_offset < 0)
    {
        print_last_error();
        sonnetdb_mq_close(queue);
        sonnetdb_close(connection);
        return 1;
    }
    printf("mq ack next offset: %lld\n", (long long)next_offset);

    sonnetdb_mq_result* mq_stats = sonnetdb_mq_stats(queue);
    require_mq_result(mq_stats);
    print_mq_json("mq stats", mq_stats);
    sonnetdb_mq_result_free(mq_stats);
    sonnetdb_mq_close(queue);

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
