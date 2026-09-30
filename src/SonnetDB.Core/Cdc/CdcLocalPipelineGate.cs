using System.Runtime.CompilerServices;

namespace SonnetDB.Cdc;

internal static class CdcLocalPipelineGate
{
    private static readonly ConditionalWeakTable<CdcEventSpool, SemaphoreSlim> Gates = new();

    internal static SemaphoreSlim For(CdcEventSpool spool)
        => Gates.GetValue(spool, static _ => new SemaphoreSlim(1, 1));
}
