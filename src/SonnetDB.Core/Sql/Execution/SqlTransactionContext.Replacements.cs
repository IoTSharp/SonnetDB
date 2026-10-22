namespace SonnetDB.Sql.Execution;

public sealed partial class SqlTransactionContext
{
    private HashSet<object>? _replacementStoreIdentities;

    /// <summary>按实际存储身份记录替换，避免 session 临时表与同名持久表共享此状态。</summary>
    internal void MarkReplacement(object storeIdentity)
    {
        ArgumentNullException.ThrowIfNull(storeIdentity);
        ThrowIfCompleted();
        var identities = _replacementStoreIdentities ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (identities.Add(storeIdentity))
            _undo.Add(() => identities.Remove(storeIdentity));
    }

    /// <summary>只有实际包含未回滚 REPLACE 的存储批次使用最终状态唯一键验证。</summary>
    internal bool UsesReplacementSemantics(object storeIdentity)
        => !_completed && _replacementStoreIdentities?.Contains(storeIdentity) == true;
}
