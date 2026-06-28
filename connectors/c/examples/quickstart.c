#include <stdio.h>
#include <stdlib.h>

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

int main(void)
{
    sonnetdb_connection* connection = sonnetdb_open("./data-c");
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
