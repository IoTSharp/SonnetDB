using SonnetDB.Data.Documents;

namespace SonnetDB.CAP.Storage.Document;

/// <summary>CAP 文档存储选项；通过 SonnetDB.Data 统一访问嵌入式与远程 Document Store。</summary>
public sealed class SonnetDbCapDocumentOptions
{
    /// <summary>SonnetDB 连接字符串；提供 Client 时不使用此属性。</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>可选的已有文档客户端；调用方负责其生命周期，适配层不会释放它。</summary>
    public SndbDocumentClient? Client { get; set; }

    /// <summary>消息、锁和可选业务批提交共用的 collection 名称。</summary>
    public string CollectionName { get; set; } = "cap";

    /// <summary>时钟，默认使用系统 UTC 时间。</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}
