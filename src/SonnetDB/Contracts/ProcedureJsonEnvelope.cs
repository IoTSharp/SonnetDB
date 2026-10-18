namespace SonnetDB.Contracts;

/// <summary>REST 存储过程的源生成 JSON 信封；payload 是同版本的严格二进制类型化合同。</summary>
internal sealed class ProcedureJsonEnvelope
{
    public int Version { get; init; }
    public byte[] Payload { get; init; } = [];
}
