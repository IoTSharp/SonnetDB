using System.Data.Common;

namespace SonnetDB.Data;

/// <summary>
/// <see cref="SndbConnection"/> 的连接字符串解析器。同时承载嵌入式与远程两种模式。
/// </summary>
/// <remarks>
/// <para>支持的键（大小写不敏感）：</para>
/// <list type="table">
///   <listheader><term>键</term><description>含义</description></listheader>
///   <item><term><c>Mode</c></term><description>显式指定 <see cref="SndbProviderMode.Embedded"/> 或 <see cref="SndbProviderMode.Remote"/>；省略时按 <c>Data Source</c> 推断。</description></item>
///   <item><term><c>Data Source</c></term><description>
///     嵌入式：本地目录路径（如 <c>./data</c> 或 <c>sonnetdb://./data</c>）。
///     远程：服务器 URL，scheme 必须为 <c>http</c>/<c>https</c>/<c>sonnetdb+http</c>/<c>sonnetdb+https</c>，
///     例如 <c>sonnetdb+http://127.0.0.1:5050/mydb</c>，URL 路径段会被解析为 <see cref="Database"/>。
///   </description></item>
///   <item><term><c>Database</c></term><description>远程模式下的目标数据库名；若同时在 URL 路径中出现以本键为准。</description></item>
///   <item><term><c>Token</c></term><description>远程模式下的 Bearer token。</description></item>
///   <item><term><c>Timeout</c></term><description>远程模式下 HTTP 请求超时（秒），默认 100。</description></item>
///   <item><term><c>Protocol</c></term><description>
///     远程模式下的线传输：<c>auto</c>（默认，运行时探测帧协议、回落 REST）、
///     <c>frame-http2</c>（强制二进制帧）、<c>rest</c>（强制 REST/JSON）。仅远程模式生效。
///   </description></item>
/// </list>
/// </remarks>
public sealed class SndbConnectionStringBuilder : DbConnectionStringBuilder
{
    private const string _keyMode = "Mode";
    private const string _keyDataSource = "Data Source";
    private const string _keyDatabase = "Database";
    private const string _keyToken = "Token";
    private const string _keyTimeout = "Timeout";
    private const string _keyProtocol = "Protocol";

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

    /// <summary>远程模式下 HTTP 请求超时（秒），默认 100。</summary>
    public int Timeout
    {
        get => TryGetValue(_keyTimeout, out var v) && int.TryParse(v?.ToString(), out var t) ? t : 100;
        set => base[_keyTimeout] = value;
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

    private static SndbTransportProtocol ParseProtocol(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "frame-http2" or "frame" or "http2" => SndbTransportProtocol.FrameHttp2,
            "rest" or "json" => SndbTransportProtocol.Rest,
            "auto" or "" => SndbTransportProtocol.Auto,
            _ => throw new FormatException($"无效的 Protocol 值 '{value}'，应为 auto / frame-http2 / rest。"),
        };

    /// <summary>
    /// 推断当前连接字符串应使用的运行模式：优先取 <see cref="Mode"/>，其次按 <see cref="DataSource"/> scheme。
    /// </summary>
    public SndbProviderMode ResolveMode()
    {
        if (Mode is { } explicitMode) return explicitMode;

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
}
