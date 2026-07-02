using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;

namespace Testcontainers.SonnetDB;

/// <summary>
/// SonnetDB Testcontainers 模块的不可变容器配置。
/// </summary>
public sealed class SonnetDbConfiguration : ContainerConfiguration
{
    /// <summary>
    /// 初始化空的 SonnetDB 模块配置。
    /// </summary>
    /// <param name="database">启动后自动创建并用于默认连接字符串的数据库名。</param>
    /// <param name="adminToken">用于创建数据库和访问远程 API 的 admin Bearer token。</param>
    /// <param name="dataRoot">容器内 SonnetDB 数据根目录。</param>
    /// <param name="autoLoadExistingDatabases">启动时是否自动加载数据根目录下已有数据库。</param>
    /// <param name="allowAnonymousProbes">是否允许匿名访问 <c>/healthz</c> 和 <c>/metrics</c>。</param>
    /// <param name="timeZone">容器时区。</param>
    /// <param name="timeoutSeconds">生成 ADO.NET 远程连接字符串时使用的 HTTP 超时秒数。</param>
    public SonnetDbConfiguration(
        string? database = null,
        string? adminToken = null,
        string? dataRoot = null,
        bool? autoLoadExistingDatabases = null,
        bool? allowAnonymousProbes = null,
        string? timeZone = null,
        int? timeoutSeconds = null)
        : base(
            image: null,
            imagePullPolicy: null,
            name: null,
            hostname: null,
            macAddress: null,
            workingDirectory: null,
            entrypoint: null,
            command: null,
            environments: null,
            exposedPorts: null,
            portBindings: null,
            resourceMappings: null,
            containers: null,
            mounts: null,
            networks: null,
            networkAliases: null,
            extraHosts: null,
            outputConsumer: null,
            waitStrategies: null,
            startupCallback: null,
            connectionStringProvider: null,
            autoRemove: null,
            privileged: null)
    {
        Database = database;
        AdminToken = adminToken;
        DataRoot = dataRoot;
        AutoLoadExistingDatabases = autoLoadExistingDatabases;
        AllowAnonymousProbes = allowAnonymousProbes;
        TimeZone = timeZone;
        TimeoutSeconds = timeoutSeconds;
    }

    /// <summary>
    /// 从通用 Docker 资源配置复制 SonnetDB 配置。
    /// </summary>
    /// <param name="resourceConfiguration">通用 Docker 资源配置。</param>
    public SonnetDbConfiguration(IResourceConfiguration<CreateContainerParameters> resourceConfiguration)
        : base(resourceConfiguration)
    {
    }

    /// <summary>
    /// 从通用容器配置复制 SonnetDB 配置。
    /// </summary>
    /// <param name="resourceConfiguration">通用容器配置。</param>
    public SonnetDbConfiguration(IContainerConfiguration resourceConfiguration)
        : base(resourceConfiguration)
    {
    }

    /// <summary>
    /// 复制已有 SonnetDB 配置。
    /// </summary>
    /// <param name="resourceConfiguration">已有 SonnetDB 配置。</param>
    public SonnetDbConfiguration(SonnetDbConfiguration resourceConfiguration)
        : this(new SonnetDbConfiguration(), resourceConfiguration)
    {
    }

    /// <summary>
    /// 合并两份 SonnetDB 配置。
    /// </summary>
    /// <param name="oldValue">旧配置。</param>
    /// <param name="newValue">新配置。</param>
    public SonnetDbConfiguration(SonnetDbConfiguration oldValue, SonnetDbConfiguration newValue)
        : base(oldValue, newValue)
    {
        Database = BuildConfiguration.Combine(oldValue.Database, newValue.Database);
        AdminToken = BuildConfiguration.Combine(oldValue.AdminToken, newValue.AdminToken);
        DataRoot = BuildConfiguration.Combine(oldValue.DataRoot, newValue.DataRoot);
        AutoLoadExistingDatabases = BuildConfiguration.Combine(oldValue.AutoLoadExistingDatabases, newValue.AutoLoadExistingDatabases);
        AllowAnonymousProbes = BuildConfiguration.Combine(oldValue.AllowAnonymousProbes, newValue.AllowAnonymousProbes);
        TimeZone = BuildConfiguration.Combine(oldValue.TimeZone, newValue.TimeZone);
        TimeoutSeconds = BuildConfiguration.Combine(oldValue.TimeoutSeconds, newValue.TimeoutSeconds);
    }

    /// <summary>
    /// 启动后自动创建并用于默认连接字符串的数据库名。
    /// </summary>
    public string? Database { get; }

    /// <summary>
    /// 用于创建数据库和访问远程 API 的 admin Bearer token。
    /// </summary>
    public string? AdminToken { get; }

    /// <summary>
    /// 容器内 SonnetDB 数据根目录。
    /// </summary>
    public string? DataRoot { get; }

    /// <summary>
    /// 启动时是否自动加载数据根目录下已有数据库。
    /// </summary>
    public bool? AutoLoadExistingDatabases { get; }

    /// <summary>
    /// 是否允许匿名访问 <c>/healthz</c> 和 <c>/metrics</c>。
    /// </summary>
    public bool? AllowAnonymousProbes { get; }

    /// <summary>
    /// 容器时区。
    /// </summary>
    public string? TimeZone { get; }

    /// <summary>
    /// 生成 ADO.NET 远程连接字符串时使用的 HTTP 超时秒数。
    /// </summary>
    public int? TimeoutSeconds { get; }
}
