#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include "sonnetdb.h"

/* Engineering probe: a fresh data directory is required. */
int main(int argc, char** argv)
{
    sonnetdb_connection* connection = NULL;
    sonnetdb_result* result = NULL;
    sonnetdb_result* second = NULL;
    int exit_code = 1;
    char error[512];
    const char* utf8 = "\xe4\xbd\xa0\xe5\xa5\xbd";

#define CHECK(condition) do { if (!(condition)) { \
    fprintf(stderr, "Failed at line %d: %s\n", __LINE__, #condition); goto cleanup; \
} } while (0)

    if (argc != 2) {
        fprintf(stderr, "Usage: sonnetdb_result_lifetime <fresh-data-directory>\n");
        return 2;
    }
    connection = sonnetdb_open(argv[1]);
    CHECK(connection != NULL);
    CHECK(sonnetdb_abi_version() == SONNETDB_ABI_VERSION);
    result = sonnetdb_execute(connection,
        "CREATE TABLE Devices (DeviceID INT, Name STRING, PRIMARY KEY (DeviceID))");
    CHECK(result != NULL);
    CHECK(sonnetdb_result_get_state(result) == SONNETDB_RESULT_READY);
    CHECK(sonnetdb_result_next(result) == 0);
    CHECK(sonnetdb_result_get_state(result) == SONNETDB_RESULT_END);
    sonnetdb_result_free(result);
    result = sonnetdb_execute(connection,
        "INSERT INTO Devices (DeviceID, Name) VALUES (9223372036854775807, '\xe4\xbd\xa0\xe5\xa5\xbd'), (2, NULL)");
    CHECK(result != NULL);
    CHECK(sonnetdb_result_get_state(result) == SONNETDB_RESULT_READY);
    CHECK(sonnetdb_result_records_affected(result) == 2);
    CHECK(sonnetdb_result_next(result) == 0);
    CHECK(sonnetdb_result_get_state(result) == SONNETDB_RESULT_END);
    sonnetdb_result_free(result);
    result = sonnetdb_execute(connection, "SELECT DeviceID, Name FROM Devices ORDER BY DeviceID DESC");
    CHECK(result != NULL);
    CHECK(sonnetdb_result_get_state(result) == SONNETDB_RESULT_READY);
    CHECK(sonnetdb_result_column_count(result) == 2);
    CHECK(strcmp(sonnetdb_result_column_name(result, 0), "DeviceID") == 0);
    CHECK(sonnetdb_result_next(result) == 1);
    CHECK(sonnetdb_result_get_state(result) == SONNETDB_RESULT_ROW);
    CHECK(sonnetdb_result_value_int64(result, 0) == INT64_MAX);
    CHECK(strcmp(sonnetdb_result_value_text(result, 1), utf8) == 0);
    CHECK(sonnetdb_result_cancel(result) == 0);
    CHECK(sonnetdb_result_next(result) == -1);
    CHECK(sonnetdb_last_error(error, (int32_t)sizeof(error)) > 0);
    CHECK(sonnetdb_result_get_state(result) == SONNETDB_RESULT_FAILED);
    second = sonnetdb_execute(connection, "SELECT DeviceID FROM Devices WHERE DeviceID = 2");
    CHECK(second != NULL);
    CHECK(sonnetdb_result_get_state(second) == SONNETDB_RESULT_READY);
    sonnetdb_result_free(result);
    result = NULL;
    CHECK(sonnetdb_result_next(second) == 1);
    CHECK(sonnetdb_result_get_state(second) == SONNETDB_RESULT_ROW);
    CHECK(sonnetdb_result_value_int64(second, 0) == 2);
    CHECK(sonnetdb_flush(connection) == 0);
    sonnetdb_close(connection);
    connection = NULL;
    CHECK(sonnetdb_result_next(second) == -1);
    CHECK(sonnetdb_last_error(error, (int32_t)sizeof(error)) > 0);
    CHECK(sonnetdb_result_records_affected(second) == -1);
    CHECK(sonnetdb_last_error(error, (int32_t)sizeof(error)) > 0);
    sonnetdb_result_free(second);
    second = NULL;

    connection = sonnetdb_open(argv[1]);
    CHECK(connection != NULL);
    result = sonnetdb_execute(connection, "SELECT DeviceID, Name FROM Devices ORDER BY DeviceID DESC");
    CHECK(result != NULL);
    CHECK(sonnetdb_result_next(result) == 1);
    CHECK(sonnetdb_result_value_int64(result, 0) == INT64_MAX);
    CHECK(sonnetdb_result_next(result) == 1);
    CHECK(sonnetdb_result_value_type(result, 1) == SONNETDB_TYPE_NULL);
    CHECK(sonnetdb_result_value_text(result, 1) == NULL);
    CHECK(sonnetdb_result_next(result) == 0);
    CHECK(sonnetdb_result_get_state(result) == SONNETDB_RESULT_END);
    CHECK(sonnetdb_result_next(result) == 0);
    CHECK(sonnetdb_result_value_type(result, 0) == -1);
    CHECK(sonnetdb_last_error(error, (int32_t)sizeof(error)) > 0);
    puts("PASS: int64, UTF-8, NULL, early free, parent close, EOF and reopen");
    exit_code = 0;

cleanup:
    if (exit_code != 0 && sonnetdb_last_error(error, (int32_t)sizeof(error)) > 0)
        fprintf(stderr, "%s\n", error);
    if (result != NULL) sonnetdb_result_free(result);
    if (second != NULL) sonnetdb_result_free(second);
    if (connection != NULL) sonnetdb_close(connection);
    return exit_code;
}
