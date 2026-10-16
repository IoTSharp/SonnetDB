using System.Text;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Data.Internal;

/// <summary>按 SQL 词法边界预检只读多结果查询，并冻结延迟执行使用的参数。</summary>
internal static class SqlSelectBatch
{
    internal const int MaximumStatements = 128;
    internal const int MaximumSqlLength = 1024 * 1024;

    public static IReadOnlyList<string>? TryCreate(
        string sql,
        SndbParameterCollection parameters,
        bool allowNamedParameterOrdinalFallback,
        CancellationToken cancellationToken,
        out SndbParameterCollection? frozenParameters)
    {
        frozenParameters = null;
        if (!sql.Contains(';'))
            return null;
        cancellationToken.ThrowIfCancellationRequested();
        // 例程定义也可能包含分号；首 token 使用相同 lexer 跳过所有已有注释语法。
        if (new SqlLexer(sql).NextToken().Kind is not (TokenKind.KeywordSelect or TokenKind.KeywordWith))
            return null;
        var tokens = new List<Token>();
        var lexer = new SqlLexer(sql);
        Token next;
        bool separatorSeen = false;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            next = lexer.NextToken();
            if (separatorSeen && next.Kind is not (TokenKind.Semicolon or TokenKind.EndOfFile)
                && sql.Length > MaximumSqlLength)
                throw new NotSupportedException($"多结果查询 SQL 不能超过 {MaximumSqlLength} 个字符。");
            separatorSeen |= next.Kind == TokenKind.Semicolon;
            tokens.Add(next);
        } while (next.Kind != TokenKind.EndOfFile);
        var slices = new List<(int Start, int End)>();
        int start = -1;
        foreach (Token token in tokens)
        {
            if (token.Kind is TokenKind.Semicolon or TokenKind.EndOfFile)
            {
                if (start >= 0)
                {
                    slices.Add((start, token.Position));
                    if (slices.Count > MaximumStatements)
                        throw new NotSupportedException($"多结果查询最多允许 {MaximumStatements} 条 SELECT。");
                    start = -1;
                }
            }
            else if (start < 0)
                start = token.Position;
        }
        if (slices.Count <= 1)
            return null;

        var frozen = new SndbParameterCollection();
        foreach (SndbParameter parameter in parameters.Items)
        {
            frozen.Add(new SndbParameter(parameter.ParameterName, FreezeValue(parameter.Value))
            {
                DbType = parameter.DbType,
                Direction = parameter.Direction,
                IsNullable = parameter.IsNullable,
                Size = parameter.Size,
                Precision = parameter.Precision,
                Scale = parameter.Scale,
            });
        }

        var statements = new List<string>(slices.Count);
        var parameterNames = tokens.Where(static token => token.Kind == TokenKind.Parameter && token.Text.Length != 0)
            .Select(static token => token.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int parameterOrdinal = 0;
        int tokenIndex = 0;
        foreach (var slice in slices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = new StringBuilder();
            int copiedThrough = slice.Start;
            while (tokenIndex < tokens.Count && tokens[tokenIndex].Position < slice.End)
            {
                Token token = tokens[tokenIndex++];
                if (token.Kind != TokenKind.Parameter)
                    continue;
                int ordinal = parameterOrdinal++;
                // 嵌入式命名参数沿用 Core 的名称优先、缺名按出现序号回退合同；
                // 远程仍由既有 ParameterBinder 严格按名称绑定，不能因批次切分放宽。
                if (token.Text.Length != 0
                    && (!allowNamedParameterOrdinalFallback || parameters.Contains(token.Text)))
                    continue;
                if (ordinal >= parameters.Count)
                    throw new InvalidOperationException($"未提供位置参数 {ordinal} 的值。");
                string name = "__sndb_batch_" + ordinal;
                while (frozen.Contains(name) || parameterNames.Contains(name))
                    name += "_";
                frozen.AddWithValue(name, FreezeValue(parameters[ordinal].Value));
                text.Append(sql.AsSpan(copiedThrough, token.Position - copiedThrough));
                text.Append('@').Append(name);
                copiedThrough = token.Position + token.Text.Length + 1;
            }
            text.Append(sql.AsSpan(copiedThrough, slice.End - copiedThrough));
            string statement = text.ToString();
            if (SqlParser.Parse(statement) is not SelectStatement)
                throw new NotSupportedException("多结果查询只允许 SELECT；不能混入写入、DDL 或事务控制语句。");
            var statementTokens = SqlLexer.Tokenize(statement);
            for (int index = 1; index < statementTokens.Count; index++)
                if (statementTokens[index - 1].Kind == TokenKind.KeywordFor
                    && statementTokens[index].Kind == TokenKind.KeywordUpdate)
                    throw new NotSupportedException("多结果只读批次不能包含锁定读；请在显式事务中单独执行 FOR UPDATE。");
            statements.Add(statement);
        }
        cancellationToken.ThrowIfCancellationRequested();
        frozenParameters = frozen;
        return statements;
    }

    private static object? FreezeValue(object? value) => value switch
    {
        byte[] bytes => bytes.ToArray(),
        float[] vector => vector.ToArray(),
        Memory<float> vector => vector.ToArray(),
        ReadOnlyMemory<float> vector => vector.ToArray(),
        _ => value,
    };
}
