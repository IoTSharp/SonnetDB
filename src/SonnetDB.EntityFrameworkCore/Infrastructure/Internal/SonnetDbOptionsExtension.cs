using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.EntityFrameworkCore.Extensions;

namespace SonnetDB.EntityFrameworkCore.Infrastructure.Internal;

/// <summary>
/// 保存 SonnetDB Provider 的 EF Core 配置扩展。
/// </summary>
public sealed class SonnetDbOptionsExtension : RelationalOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    /// <summary>
    /// 创建空的 SonnetDB Provider 配置扩展。
    /// </summary>
    public SonnetDbOptionsExtension()
    {
    }

    private SonnetDbOptionsExtension(SonnetDbOptionsExtension copyFrom)
        : base(copyFrom)
    {
    }

    /// <inheritdoc />
    public override DbContextOptionsExtensionInfo Info
        => _info ??= new ExtensionInfo(this);

    /// <inheritdoc />
    protected override RelationalOptionsExtension Clone()
        => new SonnetDbOptionsExtension(this);

    /// <inheritdoc />
    public override void ApplyServices(IServiceCollection services)
        => services.AddEntityFrameworkSonnetDB();

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : RelationalExtensionInfo(extension)
    {
        private string? _logFragment;

        private new SonnetDbOptionsExtension Extension
            => (SonnetDbOptionsExtension)base.Extension;

        public override bool IsDatabaseProvider => true;

        public override string LogFragment
            => _logFragment ??= string.IsNullOrWhiteSpace(Extension.ConnectionString)
                ? "using SonnetDB "
                : "using SonnetDB ";

        public override int GetServiceProviderHashCode()
            => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            => debugInfo["SonnetDB:" + nameof(SonnetDbOptionsExtension)] = "1";
    }
}
