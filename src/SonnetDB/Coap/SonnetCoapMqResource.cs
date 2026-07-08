using CoAP;
using CoAP.Server.Routing;

namespace SonnetDB.Coap;

/// <summary>
/// SonnetMQ 消息的 CoAP Observe resource。
/// </summary>
[CoapResource]
[CoapRoute("db/{db}/mq/{topic}")]
[CoapResourceTitle("SonnetDB MQ observe")]
[CoapResourceType("sonnetdb.mq.observe")]
[CoapInterfaceDescription("core.obs")]
internal sealed class SonnetCoapMqResource : CoapResourceBase
{
    private readonly SonnetCoapMqObserveManager _manager;

    /// <summary>
    /// 创建 SonnetMQ CoAP Observe resource。
    /// </summary>
    /// <param name="manager">MQ Observe 管理器。</param>
    public SonnetCoapMqResource(SonnetCoapMqObserveManager manager)
    {
        _manager = manager;
    }

    /// <summary>
    /// 建立或刷新 CoAP Observe 订阅，并在通知时返回下一条 MQ 消息体。
    /// </summary>
    /// <param name="db">目标数据库名。</param>
    /// <param name="topic">目标 MQ topic。</param>
    /// <param name="context">当前 CoAP 路由上下文。</param>
    /// <returns>当前订阅游标对应的消息响应。</returns>
    [CoapObserve]
    [CoapProduces(MediaType.ApplicationOctetStream, MediaType.TextPlain, MediaType.ApplicationJson)]
    public CoapRouteResult Get(string db, string topic, CoapRouteContext context)
        => _manager.Get(db, topic, context);
}
