using System.Data.Common;

namespace SonnetDB.Data;

/// <summary>
/// <see cref="SndbConnection"/> 的连接字符串解析器。同时承载嵌入式与远程两种模式。
/// </summary>
/// <remarks>
/// <para>支持的键（大小写不敏感）：</para>
/// <list type="table">
///   <listheader><term>键</term><description>含义</description></listheader>
///   <item><term><c>Mode</c></term><description>显式指定 <see cref="SndbProviderMode.Embedded"/> 或 <see cref="SndbProviderMode.Remote"/>；省略时按 <c>Host</c>/<c>Data Source</c> 推断。</description></item>
///   <item><term><c>Host</c></term><description>远程模式下服务器主机名（PostgreSQL 风格）。给定 <c>Host</c> 即视为远程连接，基址由 <c>Host</c>+<c>Port</c>+<c>Ssl</c> 组成。</description></item>
///   <item><term><c>Port</c></term><description>远程模式下服务器端口，默认 <c>5080</c>。仅在使用 <c>Host</c> 时生效。</description></item>
///   <item><term><c>Username</c></term><description>远程模式下的用户名（PostgreSQL 风格）。给定 <c>Username</c> 时客户端使用 HTTP Basic 认证；未给定则回退到 <c>Token</c>（Bearer）。别名：<c>User ID</c>、<c>UserId</c>、<c>Uid</c>。</description></item>
///   <item><term><c>Password</c></term><description>远程模式下与 <c>Username</c> 配对的密码。别名：<c>Pwd</c>。</description></item>
///   <item><term><c>Ssl</c></term><description>使用 <c>Host</c> 时是否走 https。默认 <c>false</c>（http）。别名：<c>Ssl Mode</c>（<c>require</c>/<c>disable</c>）。</description></item>
///   <item><term><c>Data Source</c></term><description>
///     嵌入式：本地目录路径（如 <c>./data</c> 或 <c>sonnetdb://./data</c>）。
///     远程（旧格式，仍支持）：服务器 URL，scheme 必须为 <c>http</c>/<c>https</c>/<c>sonnetdb+http</c>/<c>sonnetdb+https</c>，
///     例如 <c>sonnetdb+http://127.0.0.1:5050/mydb</c>，URL 路径段会被解析为 <see cref="Database"/>。
///   </description></item>
///   <item><term><c>Database</c></term><description>目标数据库名；若同时在 <c>Data Source</c> URL 路径中出现以本键为准。</description></item>
///   <item><term><c>Token</c></term><description>远程模式下的 Bearer token（旧认证方式，仍支持；当未提供 <c>Username</c> 时使用）。</description></item>
///   <item><term><c>Timeout</c></term><description>远程模式下 HTTP 请求超时（秒），默认 100。</description></item>
///   <item><term><c>Use Memory Mapped Segments</c></term><description>嵌入式模式下是否对大段启用 mmap 读取；默认跟随引擎配置。</description></item>
///   <item><term><c>Memory Mapped Segment Threshold</c></term><description>嵌入式模式下启用 mmap 的段大小阈值，支持字节数或 <c>KB</c>/<c>MB</c>/<c>GB</c> 后缀。</description></item>
///   <item><term><c>Protocol</c></term><description>
///     远程模式下的线传输：<c>auto</c>（默认，运行时探测帧协议、回落 REST）、
///     <c>frame-http2</c>（强制二进制帧）、<c>rest</c>（强制 REST/JSON）。仅远程模式生效。
///   </description></item>
/// </list>
/// <para>
/// PostgreSQL 风格示例：<c>Host=127.0.0.1;Port=5080;Database=mydb;Username=alice;Password=secret</c>。
/// 旧风格示例：<c>Data Source=sonnetdb+http://127.0.0.1:5080/mydb;Token=xxx</c>。两者均可用。
/// </para>
/// </remarks>
public sealed class SndbConnectionStringBuilder : DbConnectionStringBuilder
{
    private const string _keyMode = "Mode";
    private const string _keyDataSource = "Data Source";
    private const string _keyDatabase = "Database";
    private const string _keyToken = "Token";
    private const string _keyTimeout = "Timeout";
    private const string _keyProtocol = "Protocol";
    private const string _keyHost = "Host";
    private const string _keyPort = "Port";
    private const string _keyUsername = "Username";
    private const string _keyPassword = "Password";
    private const string _keySsl = "Ssl";
    private const string _keyUseMemoryMappedSegments = "Use Memory Mapped Segments";
    private const string _keyMemoryMappedSegmentThreshold = "Memory Mapped Segment Threshold";

    /// <summary>远程模式默认端口。</summary>
    public const int DefaultPort = 5080;

    /// <summary>使用空连接字符串构造。</summary>
    public SndbConnectionStringBuilder() { }

    /// <summary>用已有的连接字符串构造。</summary>
    public SndbConnectionStringBuilder(string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
            ConnectionString = connectionString;
    }

    /// <summary>显式模式；未设置时由 <see cref="ResolveMode"/> 按 <see cref="DataSource"/> 推断。</summary>
    public SndbProviderMode? Mode
    {
        get
        {
            if (!TryGetValue(_keyMode, out var raw) || raw is null) return null;
            var s = raw.ToString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            return Enum.TryParse<SndbProviderMode>(s, ignoreCase: true, out var m)
                ? m
                : throw new FormatException($"无效的 Mode 值 '{s}'，应为 Embedded 或 Remote。");
        }
        set
        {
            if (value is null) Remove(_keyMode);
            else base[_keyMode] = value.Value.ToString();
        }
    }

    /// <summary>原始 <c>Data Source</c> 值（路径或 URL）。</summary>
    public string DataSource
    {
        get => TryGetValue(_keyDataSource, out var v) ? v?.ToString() ?? string.Empty : string.Empty;
        set => base[_keyDataSource] = value;
    }

    /// <summary>远程模式下的数据库名。</summary>
    public string? Database
    {
        get => TryGetValue(_keyDatabase, out var v) ? v?.ToString() : null;
        set
        {
            if (value is null) Remove(_keyDatabase);
            else base[_keyDatabase] = value;
        }
    }

    /// <summary>远程模式下的 Bearer token。</summary>
    public string? Token
    {
        get => TryGetValue(_keyToken, out var v) ? v?.ToString() : null;
        set
        {
            if (value is null) Remove(_keyToken);
            else base[_keyToken] = value;
        }
    }

    /// <summary>远程模式下的服务器主机名（PostgreSQL 风格）。给定时按远程模式处理。</summary>
    public string? Host
    {
        get => TryGetValue(_keyHost, out var v) ? v?.ToString() : null;
        set
        {
            if (value is null) Remove(_keyHost);
            else base[_keyHost] = value;
        }
    }

    /// <summary>远程模式下的服务器端口（PostgreSQL 风格），默认 <see cref="DefaultPort"/>。仅在使用 <see cref="Host"/> 时生效。</summary>
    public int Port
    {
        get => TryGetValue(_keyPort, out var v) && int.TryParse(v?.ToString(), out var p) ? p : DefaultPort;
        set => base[_keyPort] = value;
    }

    /// <summary>远程模式下的用户名（PostgreSQL 风格）。给定时客户端走 HTTP Basic 认证。别名：User ID / UserId / Uid。</summary>
    public string? Username
    {
        get => ReadFirst(_keyUsername, "User ID", "UserId", "Uid");
        set
        {
            if (value is null) Remove(_keyUsername);
            else base[_keyUsername] = value;
        }
    }

    /// <summary>远程模式下与 <see cref="Username"/> 配对的密码。别名：Pwd。</summary>
    public string? Password
    {
        get => ReadFirst(_keyPassword, "Pwd");
        set
        {
            if (value is null) Remove(_keyPassword);
            else base[_keyPassword] = value;
        }
    }

    /// <summary>使用 <see cref="Host"/> 时是否走 https。默认 <c>false</c>（http）。别名：Ssl Mode（require/disable）。</summary>
    public bool Ssl
    {
        get
        {
            var raw = ReadFirst(_keySsl, "Ssl Mode", "SslMode");
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return raw.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "require" or "required" or "yes" or "on" => true,
                _ => false,
            };
        }
        set => base[_keySsl] = value;
    }

    /// <summary>远程模式下 HTTP 请求超时（秒），默认 100。</summary>
    public int Timeout
    {
        get => TryGetValue(_keyTimeout, out var v) && int.TryParse(v?.ToString(), out var t) ? t : 100;
        set => base[_keyTimeout] = value;
    }

    /// <summary>
    /// 嵌入式模式下是否对达到阈值的大段使用 memory-mapped 读取；未设置时跟随引擎默认配置。
    /// 同一进程同目录复用同一引擎实例，因此该值只在目录首次打开时生效。
    /// </summary>
    public bool? UseMemoryMappedSegments
    {
        get
        {
            var raw = ReadFirst(_keyUseMemoryMappedSegments, "UseMemoryMappedSegments", "Use Mmap Segments", "UseMmapSegments");
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return ParseBoolean(raw, _keyUseMemoryMappedSegments);
        }
        set
        {
            if (value is null) Remove(_keyUseMemoryMappedSegments);
            else base[_keyUseMemoryMappedSegments] = value.Value;
        }
    }

    /// <summary>
    /// 嵌入式模式下启用 mmap 的段大小阈值（字节）；未设置时跟随引擎默认配置。
    /// 支持连接串写法如 <c>67108864</c>、<c>64MB</c> 或 <c>1GB</c>。
    /// </summary>
    public long? MemoryMappedSegmentThresholdBytes
    {
        get
        {
            var raw = ReadFirst(
                _keyMemoryMappedSegmentThreshold,
                "MemoryMappedSegmentThreshold",
                "MemoryMappedSegmentThresholdBytes",
                "Mmap Segment Threshold",
                "MmapSegmentThreshold",
                "MmapSegmentThresholdBytes");
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return ParseByteSize(raw, _keyMemoryMappedSegmentThreshold);
        }
        set
        {
            if (value is null) Remove(_keyMemoryMappedSegmentThreshold);
            else base[_keyMemoryMappedSegmentThreshold] = value.Value;
        }
    }

    /// <summary>远程模式下的线传输选择；未设置时按 <see cref="ResolveProtocol"/> 取 <see cref="SndbTransportProtocol.Auto"/>。</summary>
    public SndbTransportProtocol? Protocol
    {
        get
        {
            if (!TryGetValue(_keyProtocol, out var raw) || raw is null) return null;
            var s = raw.ToString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            return ParseProtocol(s);
        }
        set
        {
            if (value is null) Remove(_keyProtocol);
            else base[_keyProtocol] = value.Value switch
            {
                SndbTransportProtocol.FrameHttp2 => "frame-http2",
                SndbTransportProtocol.Rest => "rest",
                _ => "auto",
            };
        }
    }

    /// <summary>
    /// 推断远程模式下的线传输：优先取 <see cref="Protocol"/>，未设置时为 <see cref="SndbTransportProtocol.Auto"/>。
    /// </summary>
    public SndbTransportProtocol ResolveProtocol() => Protocol ?? SndbTransportProtocol.Auto;

    /// <summary>
    /// 解析嵌入式模式的文件系统路径，兼容 <c>sonnetdb://path</c> 形式。
    /// </summary>
    internal string ResolveEmbeddedDataSource() => NormalizeEmbeddedDataSource(DataSource);

    /// <summary>
    /// 基于连接串构建嵌入式引擎选项；读段相关覆盖项只在该目录首次打开时生效。
    /// </summary>
    /// <param name="rootDirectory">已解析出的数据库根目录。</param>
    internal global::SonnetDB.Engine.TsdbOptions CreateEmbeddedOptions(string rootDirectory)
    {
        var readerOptions = global::SonnetDB.Storage.Segments.SegmentReaderOptions.Default;
        if (UseMemoryMappedSegments is { } useMmap)
            readerOptions = readerOptions with { UseMemoryMappedFileForLargeSegments = useMmap };
        if (MemoryMappedSegmentThresholdBytes is { } thresholdBytes)
            readerOptions = readerOptions with { MemoryMappedFileThresholdBytes = thresholdBytes };

        return new global::SonnetDB.Engine.TsdbOptions
        {
            RootDirectory = rootDirectory,
            SegmentReaderOptions = readerOptions,
        };
    }

    private static SndbTransportProtocol ParseProtocol(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "frame-http2" or "frame" or "http2" => SndbTransportProtocol.FrameHttp2,
            "rest" or "json" => SndbTransportProtocol.Rest,
            "auto" or "" => SndbTransportProtocol.Auto,
            _ => throw new FormatException($"无效的 Protocol 值 '{value}'，应为 auto / frame-http2 / rest。"),
        };

    private static bool ParseBoolean(string value, string key)
        => value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => throw new FormatException($"无效的 {key} 值 '{value}'，应为 true/false。"),
        };

    private static long ParseByteSize(string value, string key)
    {
        var raw = value.Trim();
        if (long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var bytes))
            return bytes;

        var compact = raw.Replace(" ", "", StringComparison.Ordinal);
        foreach (var (suffix, multiplier) in ByteSizeUnits)
        {
            if (!compact.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            var numberText = compact[..^suffix.Length];
            if (double.TryParse(numberText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
                && number >= 0
                && number <= long.MaxValue / (double)multiplier)
            {
                return checked((long)(number * multiplier));
            }
        }

        throw new FormatException($"无效的 {key} 值 '{value}'，应为字节数或带 KB/MB/GB 后缀的大小。");
    }

    /// <summary>
    /// 推断当前连接字符串应使用的运行模式：优先取 <see cref="Mode"/>；其次若给定
    /// <see cref="Host"/>/<see cref="Username"/> 则为远程；否则按 <see cref="DataSource"/> scheme 推断。
    /// </summary>
    public SndbProviderMode ResolveMode()
    {
        if (Mode is { } explicitMode) return explicitMode;

        // PostgreSQL 风格：给定 Host 或 Username 即视为远程连接。
        if (!string.IsNullOrWhiteSpace(Host) || !string.IsNullOrWhiteSpace(Username))
            return SndbProviderMode.Remote;

        var ds = DataSource;
        if (string.IsNullOrWhiteSpace(ds))
            return SndbProviderMode.Embedded;

        // scheme://...
        int idx = ds.IndexOf("://", StringComparison.Ordinal);
        if (idx <= 0) return SndbProviderMode.Embedded;
        var scheme = ds[..idx].ToLowerInvariant();
        return scheme switch
        {
            "http" or "https" or "sonnetdb+http" or "sonnetdb+https" => SndbProviderMode.Remote,
            _ => SndbProviderMode.Embedded,
        };
    }

    /// <summary>
    /// 解析远程连接的基址（形如 <c>http://host:port/</c>）。优先使用 PostgreSQL 风格的
    /// <see cref="Host"/>/<see cref="Port"/>/<see cref="Ssl"/>；否则回退到旧的 <see cref="DataSource"/> URL。
    /// </summary>
    /// <returns>规范化的基址，末尾带 <c>/</c>。</returns>
    /// <exception cref="InvalidOperationException">既无 <see cref="Host"/> 也无合法的远程 <see cref="DataSource"/>。</exception>
    public string ResolveBaseUrl()
    {
        var host = Host;
        if (!string.IsNullOrWhiteSpace(host))
        {
            var scheme = Ssl ? "https" : "http";
            return $"{scheme}://{host.Trim()}:{Port}/";
        }

        return ParseDataSourceEndpoint(DataSource).BaseUrl;
    }

    /// <summary>
    /// 解析远程连接的目标数据库名。优先取 <see cref="Database"/> 键；否则取旧 <see cref="DataSource"/> URL 的路径段。
    /// </summary>
    /// <returns>数据库名；无法确定时返回空字符串。</returns>
    public string ResolveDatabase()
    {
        if (!string.IsNullOrWhiteSpace(Database))
            return Database!;

        // 使用 Host 时数据库只能来自 Database 键；旧 URL 时从 path 段取。
        if (!string.IsNullOrWhiteSpace(Host))
            return string.Empty;

        return ParseDataSourceEndpoint(DataSource).DatabaseFromPath;
    }

    /// <summary>
    /// 解析旧风格 <c>Data Source</c> 远程 URL，返回 (baseUrl, databaseFromPath)。
    /// 支持 <c>sonnetdb+http://host:port/dbname</c> / <c>http://host:port/dbname</c>。
    /// </summary>
    public static (string BaseUrl, string DatabaseFromPath) ParseDataSourceEndpoint(string dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource))
            throw new InvalidOperationException("远程连接缺少 'Host' 或 'Data Source'。");

        var ds = dataSource.Trim();
        if (ds.StartsWith("sonnetdb+http://", StringComparison.OrdinalIgnoreCase))
            ds = "http://" + ds["sonnetdb+http://".Length..];
        else if (ds.StartsWith("sonnetdb+https://", StringComparison.OrdinalIgnoreCase))
            ds = "https://" + ds["sonnetdb+https://".Length..];

        if (!Uri.TryCreate(ds, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"远程 Data Source 不是合法 URL: {dataSource}");
        if (uri.Scheme != "http" && uri.Scheme != "https")
            throw new InvalidOperationException($"不支持的远程 scheme: {uri.Scheme}");

        var baseUrl = $"{uri.Scheme}://{uri.Authority}/";
        var path = uri.AbsolutePath.Trim('/');
        return (baseUrl, path);
    }

    private static string NormalizeEmbeddedDataSource(string dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource)) return dataSource;
        const string prefix = "sonnetdb://";
        if (dataSource.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return dataSource[prefix.Length..];
        return dataSource;
    }

    /// <summary>按顺序读取首个存在的键值（用于键别名，如 Username / User ID / Uid）。</summary>
    private string? ReadFirst(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (TryGetValue(key, out var v) && v is not null)
            {
                var s = v.ToString();
                if (!string.IsNullOrEmpty(s))
                    return s;
            }
        }
        return null;
    }

    private static readonly (string Suffix, long Multiplier)[] ByteSizeUnits =
    [
        ("GiB", 1024L * 1024L * 1024L),
        ("GB", 1024L * 1024L * 1024L),
        ("MiB", 1024L * 1024L),
        ("MB", 1024L * 1024L),
        ("KiB", 1024L),
        ("KB", 1024L),
        ("B", 1L),
    ];
}
