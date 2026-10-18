using System.Runtime.CompilerServices;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

internal sealed record SqlTemporaryTableIdentity(Guid SessionId, Guid ObjectId)
{
    private static readonly ConditionalWeakTable<TableSchema, SqlTemporaryTableIdentity> Identities = new();

    internal static SqlTemporaryTableIdentity Register(TableSchema schema, Guid sessionId)
    {
        SqlTemporaryTableIdentity identity = Identities.GetValue(schema,
            _ => new SqlTemporaryTableIdentity(sessionId, Guid.NewGuid()));
        if (identity.SessionId != sessionId)
            throw new InvalidOperationException("临时表 schema 不能跨连接会话复用。");
        return identity;
    }

    internal static bool TryGet(TableSchema schema, out SqlTemporaryTableIdentity? identity)
        => Identities.TryGetValue(schema, out identity);
}
