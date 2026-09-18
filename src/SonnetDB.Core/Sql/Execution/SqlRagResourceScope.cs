using SonnetDB.SemanticContent;

namespace SonnetDB.Sql.Execution;

// 对实际 manager 访问建立上下文；view/routine 展开后的访问仍受同一约束。
internal static class SqlRagResourceScope
{
    private static readonly AsyncLocal<int> Depth = new();

    internal static bool IsActive => Depth.Value != 0;

    internal static IDisposable Enter()
    {
        int previous = Depth.Value;
        Depth.Value = checked(previous + 1);
        return new Scope(previous);
    }

    internal static void Demand(string name)
    {
        if (IsActive && RagReservedResourceNames.IsReserved(name))
            throw new InvalidOperationException("RAG 内部资源只能通过授权治理入口访问，不能通过 SQL 访问。");
    }

    internal static bool IsVisible(string name)
        => !IsActive || !RagReservedResourceNames.IsReserved(name);

    private sealed class Scope(int previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            Depth.Value = previous;
            _disposed = true;
        }
    }
}
