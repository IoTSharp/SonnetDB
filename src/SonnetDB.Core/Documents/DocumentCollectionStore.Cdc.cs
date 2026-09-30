using SonnetDB.Kv;

namespace SonnetDB.Documents;

public sealed partial class DocumentCollectionStore
{
    /// <summary>
    /// 在集合写锁内取得与当前 change-feed 位点对应的稳定主数据快照。
    /// </summary>
    /// <returns>稳定 KV 快照及其对应的集合变更位点。</returns>
    internal (KvReadSnapshot Snapshot, long Checkpoint) AcquireCdcReadSnapshot()
    {
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            return (_keyspace.AcquireReadSnapshot(), ReadLatestChangeSequenceLocked());
        }
    }
}
