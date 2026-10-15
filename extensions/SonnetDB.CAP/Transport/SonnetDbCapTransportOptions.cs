using SonnetDB.Data.Mq;

namespace SonnetDB.CAP.Transport;

/// <summary>通过 SonnetDB.Data MQ 客户端访问本地或远程消息队列的 CAP 选项。</summary>
public sealed class SonnetDbCapTransportOptions
{
    /// <summary>MQ 连接字符串；嵌入式路径与文档数据库目录分别配置。</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>可选已有 MQ 客户端；由调用方负责释放。</summary>
    public SndbMqClient? Client { get; set; }

    /// <summary>用于稳定映射 topic 和 group 的应用命名空间；发布和订阅必须一致。</summary>
    public string Namespace { get; set; } = "cap";

    /// <summary>空闲拉取与拒绝重投的轮询间隔，默认 100 毫秒。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>包含消息头和正文的单条 MQ 信封字节预算，默认 1 MiB。</summary>
    public int MaxEnvelopeBytes { get; set; } = 1024 * 1024;
}
