using System.Runtime.CompilerServices;
using SonnetDB.Kv;

namespace SonnetDB.Core.Tests;

/// <summary>
/// 双引擎测试矩阵：环境变量 <c>SONNETDB_KV_ENGINE=Lsm</c> 时，测试进程中新建的 <see cref="KvOptions"/>
/// 默认使用 LSM 引擎。依赖 legacy 内部实现的测试用 <c>[Trait("KvEngine", "LegacyOnly")]</c> 标记，
/// 在 LSM 轮次中以 <c>--filter "KvEngine!=LegacyOnly"</c> 排除。
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
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (!Enum.TryParse(value, ignoreCase: true, out KvStorageEngine engine) || !Enum.IsDefined(engine))
            throw new InvalidOperationException($"{EnvironmentVariable}='{value}' is not a valid KvStorageEngine.");
        Selected = engine;
        KvStorageEngineTestDefaults.Override = engine;
    }
}
