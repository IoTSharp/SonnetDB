using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Images;

namespace Testcontainers.SonnetDB;

/// <summary>
/// SonnetDB Server 的 Testcontainers fluent builder。
/// </summary>
public sealed class SonnetDbBuilder : ContainerBuilder<SonnetDbBuilder, SonnetDbContainer, SonnetDbConfiguration>
{
    /// <summary>
    /// SonnetDB Server 默认 Docker 镜像。
    /// </summary>
    public const string SonnetDbImage = "iotsharp/sonnetdb:latest";

    /// <summary>
    /// SonnetDB Server 容器内 HTTP 端口。
    /// </summary>
    public const ushort SonnetDbPort = 5080;

    /// <summary>
    /// 默认创建并用于连接字符串的数据库名。
    /// </summary>
    public const string DefaultDatabase = "test";

    /// <summary>
    /// 默认测试 admin Bearer token。仅用于临时测试容器。
    /// </summary>
    public const string DefaultAdminToken = "sonnetdb_test_admin_token";

    /// <summary>
    /// 默认容器内数据根目录。
    /// </summary>
    public const string DefaultDataRoot = "/data";

    /// <summary>
    /// 默认容器时区。
    /// </summary>
    public const string DefaultTimeZone = "Asia/Shanghai";

    /// <summary>
    /// 默认 ADO.NET 远程连接超时秒数。
    /// </summary>
    public const int DefaultTimeoutSeconds = 30;

    /// <summary>
    /// 使用默认 SonnetDB 镜像初始化 builder。
    /// </summary>
    public SonnetDbBuilder()
        : this(SonnetDbImage)
    {
    }

    /// <summary>
    /// 使用指定镜像名初始化 builder。
    /// </summary>
    /// <param name="image">完整 Docker 镜像名，例如 <c>iotsharp/sonnetdb:latest</c>。</param>
    public SonnetDbBuilder(string image)
        : this(new DockerImage(image))
    {
    }

    /// <summary>
    /// 使用指定镜像初始化 builder。
    /// </summary>
    /// <param name="image">Testcontainers 镜像对象。</param>
    public SonnetDbBuilder(IImage image)
        : this(new SonnetDbConfiguration())
    {
        DockerResourceConfiguration = Init().WithImage(image).DockerResourceConfiguration;
    }

    private SonnetDbBuilder(SonnetDbConfiguration resourceConfiguration)
        : base(resourceConfiguration)
    {
        DockerResourceConfiguration = resourceConfiguration;
    }

    /// <summary>
    /// 获取当前 Docker 资源配置。
    /// </summary>
    protected override SonnetDbConfiguration DockerResourceConfiguration { get; }

    /// <summary>
    /// 设置启动后自动创建并用于默认连接字符串的数据库名。
    /// </summary>
    /// <param name="database">数据库名。</param>
    /// <returns>配置后的 builder。</returns>
    public SonnetDbBuilder WithDatabase(string database)
    {
        if (string.IsNullOrWhiteSpace(database))
            throw new ArgumentException("数据库名不能为空。", nameof(database));

        return Merge(DockerResourceConfiguration, new SonnetDbConfiguration(database: database));
    }

    /// <summary>
    /// 设置用于创建数据库和访问远程 API 的 admin Bearer token。
    /// </summary>
    /// <param name="adminToken">admin Bearer token。</param>
    /// <returns>配置后的 builder。</returns>
    public SonnetDbBuilder WithAdminToken(string adminToken)
    {
        if (string.IsNullOrWhiteSpace(adminToken))
            throw new ArgumentException("admin token 不能为空。", nameof(adminToken));

        return Merge(DockerResourceConfiguration, new SonnetDbConfiguration(adminToken: adminToken))
            .WithEnvironment("SONNETDB_SonnetDBServer__Tokens__" + adminToken, "admin");
    }

    /// <summary>
    /// 设置容器内 SonnetDB 数据根目录。
    /// </summary>
    /// <param name="dataRoot">容器内数据根目录。</param>
    /// <returns>配置后的 builder。</returns>
    public SonnetDbBuilder WithDataRoot(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
            throw new ArgumentException("数据根目录不能为空。", nameof(dataRoot));

        return Merge(DockerResourceConfiguration, new SonnetDbConfiguration(dataRoot: dataRoot))
            .WithEnvironment("SONNETDB_SonnetDBServer__DataRoot", dataRoot);
    }

    /// <summary>
    /// 设置启动时是否自动加载数据根目录下已有数据库。
    /// </summary>
    /// <param name="autoLoadExistingDatabases">是否自动加载已有数据库。</param>
    /// <returns>配置后的 builder。</returns>
    public SonnetDbBuilder WithAutoLoadExistingDatabases(bool autoLoadExistingDatabases)
        => Merge(DockerResourceConfiguration, new SonnetDbConfiguration(autoLoadExistingDatabases: autoLoadExistingDatabases))
            .WithEnvironment("SONNETDB_SonnetDBServer__AutoLoadExistingDatabases", autoLoadExistingDatabases.ToString().ToLowerInvariant());

    /// <summary>
    /// 设置是否允许匿名访问健康检查和指标探针。
    /// </summary>
    /// <param name="allowAnonymousProbes">是否允许匿名探针。</param>
    /// <returns>配置后的 builder。</returns>
    public SonnetDbBuilder WithAnonymousProbes(bool allowAnonymousProbes)
        => Merge(DockerResourceConfiguration, new SonnetDbConfiguration(allowAnonymousProbes: allowAnonymousProbes))
            .WithEnvironment("SONNETDB_SonnetDBServer__AllowAnonymousProbes", allowAnonymousProbes.ToString().ToLowerInvariant());

    /// <summary>
    /// 设置容器时区。
    /// </summary>
    /// <param name="timeZone">容器时区。</param>
    /// <returns>配置后的 builder。</returns>
    public SonnetDbBuilder WithTimeZone(string timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone))
            throw new ArgumentException("时区不能为空。", nameof(timeZone));

        return Merge(DockerResourceConfiguration, new SonnetDbConfiguration(timeZone: timeZone))
            .WithEnvironment("TZ", timeZone);
    }

    /// <summary>
    /// 设置生成 ADO.NET 远程连接字符串时使用的 HTTP 超时秒数。
    /// </summary>
    /// <param name="timeoutSeconds">超时秒数，必须大于 0。</param>
    /// <returns>配置后的 builder。</returns>
    public SonnetDbBuilder WithConnectionTimeout(int timeoutSeconds)
    {
        if (timeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "超时秒数必须大于 0。");

        return Merge(DockerResourceConfiguration, new SonnetDbConfiguration(timeoutSeconds: timeoutSeconds));
    }

    /// <summary>
    /// 构建 SonnetDB 容器实例。
    /// </summary>
    /// <returns>配置后的 SonnetDB 容器。</returns>
    public override SonnetDbContainer Build()
    {
        Validate();
        return new SonnetDbContainer(DockerResourceConfiguration);
    }

    /// <summary>
    /// 初始化 SonnetDB 模块默认端口、环境变量、连接字符串提供程序和健康检查。
    /// </summary>
    /// <returns>初始化后的 builder。</returns>
    protected override SonnetDbBuilder Init()
        => base.Init()
            .WithPortBinding(SonnetDbPort, true)
            .WithEnvironment("ASPNETCORE_URLS", "http://+:5080")
            .WithDatabase(DefaultDatabase)
            .WithAdminToken(DefaultAdminToken)
            .WithDataRoot(DefaultDataRoot)
            .WithAutoLoadExistingDatabases(true)
            .WithAnonymousProbes(true)
            .WithTimeZone(DefaultTimeZone)
            .WithConnectionTimeout(DefaultTimeoutSeconds)
            .WithConnectionStringProvider(new SonnetDbConnectionStringProvider())
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                .ForPort(SonnetDbPort)
                .ForPath("/healthz")));

    /// <summary>
    /// 校验 SonnetDB 容器配置。
    /// </summary>
    protected override void Validate()
    {
        base.Validate();
        if (string.IsNullOrWhiteSpace(DockerResourceConfiguration.Database))
            throw new ArgumentException("SonnetDB 默认数据库名不能为空。", nameof(DockerResourceConfiguration.Database));
        if (string.IsNullOrWhiteSpace(DockerResourceConfiguration.AdminToken))
            throw new ArgumentException("SonnetDB admin token 不能为空。", nameof(DockerResourceConfiguration.AdminToken));
        if (DockerResourceConfiguration.TimeoutSeconds is <= 0)
            throw new ArgumentOutOfRangeException(nameof(DockerResourceConfiguration.TimeoutSeconds), "连接超时秒数必须大于 0。");
    }

    /// <summary>
    /// 克隆通用 Docker 资源配置。
    /// </summary>
    /// <param name="resourceConfiguration">通用 Docker 资源配置。</param>
    /// <returns>配置后的 builder。</returns>
    protected override SonnetDbBuilder Clone(IResourceConfiguration<CreateContainerParameters> resourceConfiguration)
        => Merge(DockerResourceConfiguration, new SonnetDbConfiguration(resourceConfiguration));

    /// <summary>
    /// 克隆通用容器配置。
    /// </summary>
    /// <param name="resourceConfiguration">通用容器配置。</param>
    /// <returns>配置后的 builder。</returns>
    protected override SonnetDbBuilder Clone(IContainerConfiguration resourceConfiguration)
        => Merge(DockerResourceConfiguration, new SonnetDbConfiguration(resourceConfiguration));

    /// <summary>
    /// 合并 SonnetDB 模块配置。
    /// </summary>
    /// <param name="oldValue">旧配置。</param>
    /// <param name="newValue">新配置。</param>
    /// <returns>配置后的 builder。</returns>
    protected override SonnetDbBuilder Merge(SonnetDbConfiguration oldValue, SonnetDbConfiguration newValue)
        => new SonnetDbBuilder(new SonnetDbConfiguration(oldValue, newValue));
}
