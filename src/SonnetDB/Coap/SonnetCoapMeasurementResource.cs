using CoAP;
using CoAP.Server.Routing;

namespace SonnetDB.Coap;

/// <summary>
/// SonnetDB measurement 写入的 CoAP resource。
/// </summary>
[CoapResource]
[CoapRoute("db/{db}/m/{measurement}")]
[CoapResourceTitle("SonnetDB measurement ingest")]
[CoapResourceType("sonnetdb.measurement.ingest")]
[CoapInterfaceDescription("core.w")]
internal sealed class SonnetCoapMeasurementResource : CoapResourceBase
{
    private readonly SonnetCoapMeasurementIngestor _ingestor;

    /// <summary>
    /// 创建 CoAP measurement 写入 resource。
    /// </summary>
    /// <param name="ingestor">业务写入服务。</param>
    public SonnetCoapMeasurementResource(SonnetCoapMeasurementIngestor ingestor)
    {
        _ingestor = ingestor;
    }

    /// <summary>
    /// 处理设备通过 CoAP POST 上传的 measurement payload。
    /// </summary>
    /// <param name="db">目标数据库名。</param>
    /// <param name="measurement">目标 measurement 名称。</param>
    /// <param name="context">当前 CoAP 路由上下文。</param>
    /// <returns>写入结果响应。</returns>
    [CoapPost]
    [CoapProduces(MediaType.TextPlain)]
    public CoapRouteResult Post(string db, string measurement, CoapRouteContext context)
        => _ingestor.Ingest(db, measurement, context);

    /// <summary>
    /// 处理设备通过 CoAP PUT 上传的 measurement payload。
    /// </summary>
    /// <param name="db">目标数据库名。</param>
    /// <param name="measurement">目标 measurement 名称。</param>
    /// <param name="context">当前 CoAP 路由上下文。</param>
    /// <returns>写入结果响应。</returns>
    [CoapPut]
    [CoapProduces(MediaType.TextPlain)]
    public CoapRouteResult Put(string db, string measurement, CoapRouteContext context)
        => _ingestor.Ingest(db, measurement, context);
}
