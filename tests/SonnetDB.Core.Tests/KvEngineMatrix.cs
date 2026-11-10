using System.Runtime.CompilerServices;
using SonnetDB.Kv;

namespace SonnetDB.Core.Tests;

/// <summary>
/// 双引擎测试矩阵。产品默认引擎是 LSM（K8）；本测试程序集在未设置环境变量时仍以 legacy 为默认，
/// 保持依赖 legacy 内部实现的大量回归测试原样运行。环境变量 <c>SONNETDB_KV_ENGINE=Lsm</c> 时切换到 LSM，
/// 并以 <c>--filter "KvEngine!=LegacyOnly"</c> 排除标记为 <c>[Trait("KvEngine", "LegacyOnly")]</c> 的测试。
/// </summary>
internal static class KvEngineMatrix
{
    /// <summary>环境变量名。</summary>
    internal const string EnvironmentVariable = "SONNETDB_KV_ENGINE";

    /// <summary>本测试进程选定的默认引擎。</summary>
    internal static KvStorageEngine Selected { get; private set; } = KvStorageEngine.Legacy;

#pragma warning disable CA2255 // 测试程序集需要在任何 KvOptions 构造之前设置默认引擎。
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        string? value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        KvStorageEngine engine = KvStorageEngine.Legacy;
        if (!string.IsNullOrWhiteSpace(value)
            && (!Enum.TryParse(value, ignoreCase: true, out engine) || !Enum.IsDefined(engine)))
        {
            throw new InvalidOperationException($"{EnvironmentVariable}='{value}' is not a valid KvStorageEngine.");
        }
        Selected = engine;
        KvStorageEngineTestDefaults.Override = engine;
    }
}
