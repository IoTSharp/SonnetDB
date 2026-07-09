using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Hosting;

namespace SonnetDB.LineProtocolUdp;

/// <summary>
/// 配置 Influx Line Protocol UDP 采集入口。
/// </summary>
internal static class LineProtocolUdpBootstrap
{
    /// <summary>
    /// 在启用 UDP 采集时注册监听后台服务。
    /// </summary>
    /// <param name="builder">Web 应用构建器。</param>
    /// <param name="options">UDP 采集配置。</param>
    public static void ConfigureServices(WebApplicationBuilder builder, LineProtocolUdpOptions options)
    {
        if (!options.Enabled)
            return;

        ValidateOptions(options);
        builder.Services.AddHostedService<LineProtocolUdpListenerService>();
    }

    private static void ValidateOptions(LineProtocolUdpOptions options)
    {
        ValidatePort(options.Port, "SonnetDBServer:LineProtocolUdp:Port");
        if (string.IsNullOrWhiteSpace(options.Database) || !TsdbRegistry.IsValidName(options.Database))
            throw new InvalidOperationException("启用 Line Protocol UDP 时必须配置有效的 SonnetDBServer:LineProtocolUdp:Database。");
        if (options.MaxDatagramBytes <= 0 || options.MaxDatagramBytes > LineProtocolUdpListenerService.UdpPayloadLimit)
            throw new InvalidOperationException(
                $"SonnetDBServer:LineProtocolUdp:MaxDatagramBytes 必须位于 1..{LineProtocolUdpListenerService.UdpPayloadLimit}。");
        if (!LineProtocolUdpListenerService.TryParsePrecision(options.Precision, out _))
            throw new InvalidOperationException("SonnetDBServer:LineProtocolUdp:Precision 只支持 n/ns、u/us/µs、ms、s。");
    }

    private static void ValidatePort(int port, string name)
    {
        if (port < 0 || port > 65_535)
            throw new InvalidOperationException($"{name} 必须位于 0..65535。");
    }
}
