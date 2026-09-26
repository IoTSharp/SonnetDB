using System.Data;
using System.Data.Common;
using SonnetDB.Data;
using SonnetDB.Tables;

// 此消费者只引用安装的 NuGet 包，避免项目引用掩盖缺失的发布内容。
internal static class InsertReturningContract
{
    internal static async Task RunAsync(string connectionString, bool fullContract)
    {
        await using var connection = new SndbConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        string table = "returning_package_" + Guid.NewGuid().ToString("N")[..12];
        command.CommandText = $"CREATE TABLE {table} (id INT AUTO_INCREMENT, name STRING NOT NULL DEFAULT 'generated', rv INT ROWVERSION, note STRING NULL, PRIMARY KEY (id))";
        await command.ExecuteNonQueryAsync();

        // 返回列顺序刻意不同于表 schema；默认值和 DBNull 必须在首行可见。
        command.CommandText = $"INSERT INTO {table} (note) VALUES (NULL), ('second') RETURNING name, id, rv, note";
        using (var reader = command.ExecuteReader())
        {
            Check(reader.FieldCount == 4, "field count");
            Check(Enumerable.Range(0, 4).Select(reader.GetName).SequenceEqual(["name", "id", "rv", "note"]), "column order");
            if (fullContract) CheckSchema(reader);
            else Console.WriteLine($"Legacy metadata before Read: id={reader.GetFieldType(1).Name}; full contract is not asserted.");
            for (long id = 1; id <= 2; id++)
            {
                Check(reader.Read(), "multi-row result");
                Check(Enumerable.Range(0, 4).Select(reader.GetValue).SequenceEqual(
                    new object[] { "generated", id, 1L, id == 1 ? DBNull.Value : "second" }), "generated values and input order");
            }
            Check(!reader.Read() && reader.RecordsAffected == 2, "multi-row affected count");
        }

        if (!fullContract)
        {
            Console.WriteLine("PASS INSERT RETURNING legacy values/order/count only (not the full metadata contract)");
            return;
        }

        command.CommandText = $"INSERT INTO {table} (name) SELECT name FROM {table} WHERE id = -1 RETURNING name, id, rv, note";
        using (var reader = command.ExecuteReader())
        {
            CheckSchema(reader);
            Check(!reader.Read() && reader.RecordsAffected == 0, "empty reader/count");
        }
        Check(command.ExecuteScalar() is null && command.ExecuteNonQuery() == 0, "empty scalar/nonquery");
        await using (var reader = await command.ExecuteReaderAsync())
        {
            CheckSchema(reader);
            Check(!await reader.ReadAsync() && reader.RecordsAffected == 0, "async empty reader/count");
        }
        Check(await command.ExecuteScalarAsync() is null && await command.ExecuteNonQueryAsync() == 0, "async empty scalar/nonquery");

        command.CommandText = $"INSERT INTO {table} (name) VALUES (@name), (@other) RETURNING id";
        command.Parameters.AddWithValue("@name", "pump ' 参数");
        command.Parameters.AddWithValue("@other", "fan");
        Check(Equals(command.ExecuteScalar(), 3L), "scalar returns first row of a batch");
        Check(command.ExecuteNonQuery() == 2, "nonquery returns affected count, not identity");
        Check(Equals(await command.ExecuteScalarAsync(), 7L), "async scalar returns first row");
        Check(await command.ExecuteNonQueryAsync() == 2, "async nonquery affected count");
        command.Parameters.Clear();
        command.CommandText = $"SELECT name FROM {table} WHERE id = 3";
        Check(Equals(command.ExecuteScalar(), "pump ' 参数"), "parameter value round-trip");

        foreach (bool useAsync in new[] { false, true })
        {
            await using var transaction = useAsync
                ? await connection.BeginTransactionAsync()
                : connection.BeginTransaction();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO {table} DEFAULT VALUES RETURNING name, id, rv, note";
            await using (var reader = useAsync ? await command.ExecuteReaderAsync() : command.ExecuteReader())
            {
                CheckSchema(reader);
                Check(useAsync ? await reader.ReadAsync() : reader.Read(), "transaction generated row");
                Check(reader.GetString(0) == "generated" && reader.GetInt64(1) > 10 && reader.GetInt64(2) == 1 && reader.IsDBNull(3), "transaction defaults/generated values");
                Check(!(useAsync ? await reader.ReadAsync() : reader.Read()) && reader.RecordsAffected == 1, "transaction affected count");
            }
            if (useAsync) await transaction.RollbackAsync();
            else transaction.Rollback();
            command.Transaction = null;
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            Check(Equals(await command.ExecuteScalarAsync(), 10L), "rollback leaves no inserted row");
        }

        string composite = table + "_keys";
        command.CommandText = $"CREATE TABLE {composite} (tenant INT, id INT, PRIMARY KEY (tenant, id))";
        await command.ExecuteNonQueryAsync();
        command.CommandText = $"INSERT INTO {composite} (tenant, id) VALUES (1, 1), (1, 2) RETURNING tenant, id";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            for (long id = 1; id <= 2; id++)
                Check(await reader.ReadAsync() && reader.GetInt64(0) == 1 && reader.GetInt64(1) == id, "composite primary-key order");
            Check(!await reader.ReadAsync() && reader.RecordsAffected == 2, "composite affected count");
        }
        foreach (bool useAsync in new[] { false, true })
        {
            command.CommandText = $"INSERT INTO {composite} (tenant, id) VALUES (2, 1), (1, 1) RETURNING tenant, id";
            string? errorCode = null;
            try
            {
                if (useAsync) await command.ExecuteNonQueryAsync();
                else command.ExecuteNonQuery();
            }
            catch (TableConstraintException exception) { errorCode = exception.ErrorCode; }
            catch (SndbServerException exception) { errorCode = exception.Error; }
            Check(errorCode == "table_unique_violation", "duplicate composite key stable error code");
            command.CommandText = $"SELECT COUNT(*) FROM {composite}";
            Check(Equals(await command.ExecuteScalarAsync(), 2L), "duplicate batch has no partial write");
        }
        Console.WriteLine("PASS INSERT RETURNING full package contract: schema, generated values, order, empty results, sync/async methods, parameters, rollback, composite keys and atomic errors");
    }

    private static void CheckSchema(DbDataReader reader)
    {
        string[] names = ["name", "id", "rv", "note"];
        Type[] types = [typeof(string), typeof(long), typeof(long), typeof(string)];
        Check(reader.FieldCount == names.Length, "schema field count");
        Check(Enumerable.Range(0, 4).Select(reader.GetName).SequenceEqual(names), "schema names before Read");
        Check(Enumerable.Range(0, 4).Select(reader.GetFieldType).SequenceEqual(types), "declared types before Read");
        var schema = reader.GetSchemaTable() ?? throw new InvalidOperationException("Missing schema table.");
        Check(schema.Rows.Count == 4, "schema row count");
        for (int i = 0; i < names.Length; i++)
        {
            var row = schema.Rows[i];
            Check(Equals(row[SchemaTableColumn.ColumnName], names[i]), "schema name");
            Check(Equals(row[SchemaTableColumn.DataType], types[i]), "schema type");
            Check(Equals(row[SchemaTableColumn.AllowDBNull], i == 3), "schema nullability");
            Check(Equals(row[SchemaTableColumn.IsKey], i == 1), "schema primary key");
            Check(Equals(row[SchemaTableOptionalColumn.IsAutoIncrement], i == 1), "schema auto increment");
            Check(Equals(row["IsRowVersion"], i == 2), "schema rowversion");
        }
    }

    private static void Check(bool condition, string contract)
    {
        if (!condition) throw new InvalidOperationException("INSERT RETURNING contract failed: " + contract);
    }
}
