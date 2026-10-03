using System.Text.Json;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Samples;

/// <summary>文件导入、两种向量查询和数据库重开的本地组合验收结果。</summary>
/// <param name="ImportedRows">从真实 JSON 文件读取并保存的记录数。</param>
/// <param name="VectorId">文档向量搜索返回的记录标识。</param>
/// <param name="KnnTime">measurement KNN 返回的时间戳。</param>
/// <param name="RejectedQueries">按显式预算拒绝的查询数。</param>
public sealed record SqlBudgetJourneyResult(int ImportedRows, string VectorId, long KnnTime, int RejectedQueries);

/// <summary>真实文件读取、持久文档与 measurement 向量搜索的有界组合旅程。</summary>
public static class SqlBudgetJourney
{
    private const string VectorSql = "SELECT id FROM vector_search(source => vectors, vector_field => '$.embedding', vector => [1,0], k => 1)";
    private const string KnnSql = "SELECT time FROM knn(points, embedding, [1,0], 1)";

    /// <summary>在调用方拥有的目录内执行导入、预算拒绝、重开与一致性核对。</summary>
    /// <param name="directory">本轮独占的输出目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全部边界核对完成后的本地结果。</returns>
    public static async Task<SqlBudgetJourneyResult> RunAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        CancellationToken token = deadline.Token;
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "vectors.json");
        await File.WriteAllTextAsync(file,
            """[{"id":"a","embedding":[1,0]},{"id":"b","embedding":[0,1]},{"id":"c","embedding":[-1,0]}]""", token);
        string fileSql = $"SELECT ordinal, id, document FROM json_each('{file.Replace("'", "''", StringComparison.Ordinal)}')";
        var options = new SqlExecutionOptions
        {
            MaxMaterializedRows = 64,
            MaxMaterializedBytes = 64 * 1024,
            EnableParallelism = false,
            CancellationToken = token,
        };
        string root = Path.Combine(directory, "database");
        int rejected = 0;
        using (Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = root }))
        {
            SqlExecutor.Execute(database, "CREATE DOCUMENT COLLECTION vectors");
            SqlExecutor.Execute(database, "CREATE MEASUREMENT points (source TAG, embedding FIELD VECTOR(2))");
            SelectExecutionResult input = Select(database, fileSql, options);
            if (input.Rows.Count != 3)
                throw new InvalidOperationException("文件输入记录数不一致。");
            foreach (IReadOnlyList<object?> row in input.Rows)
            {
                token.ThrowIfCancellationRequested();
                string id = (string)row[1]!;
                string json = (string)row[2]!;
                database.Documents.Open("vectors").Insert(id, json);
                using JsonDocument document = JsonDocument.Parse(json);
                float[] embedding = document.RootElement.GetProperty("embedding").EnumerateArray()
                    .Select(static value => value.GetSingle()).ToArray();
                SqlExecutor.Execute(database, databaseName: null,
                    "INSERT INTO points (time,source,embedding) VALUES(@time,@source,@embedding)",
                    new SqlParameters().AddNamed("time", Convert.ToInt64(row[0])).AddNamed("source", id).AddNamed("embedding", embedding),
                    controlPlane: null, options: new SqlExecutionOptions { CancellationToken = token });
            }
            foreach (string sql in new[] { fileSql, VectorSql, KnnSql })
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    Select(database, sql, options with { MaxMaterializedRows = 1 });
                }
                catch (InvalidOperationException error) when (error.Message.Contains("累计物化", StringComparison.Ordinal))
                {
                    rejected++;
                    continue;
                }
                throw new InvalidOperationException("查询未按累计物化预算拒绝。");
            }
            Verify(database, options);
            database.FlushNow();
        }
        using Tsdb reopened = Tsdb.Open(new TsdbOptions { RootDirectory = root });
        Verify(reopened, options);
        return new SqlBudgetJourneyResult(3, "a", 0, rejected);
    }

    private static void Verify(Tsdb database, SqlExecutionOptions options)
    {
        SelectExecutionResult vector = Select(database, VectorSql, options);
        SelectExecutionResult knn = Select(database, KnnSql, options);
        if (vector.Rows.Count != 1 || !Equals(vector.Rows[0][0], "a")
            || knn.Rows.Count != 1 || !Equals(knn.Rows[0][0], 0L))
            throw new InvalidOperationException("重开前后向量查询结果不一致。");
    }

    private static SelectExecutionResult Select(Tsdb database, string sql, SqlExecutionOptions options)
        => SqlExecutor.Execute(database, databaseName: null, sql, parameters: null, controlPlane: null, options)
            as SelectExecutionResult ?? throw new InvalidOperationException("查询没有返回行结果。");
}
