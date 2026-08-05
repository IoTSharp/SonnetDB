namespace SonnetDB.Modbus;

/// <summary>
/// Modbus 地址的输入表示方式。
/// </summary>
public enum ModbusAddressingMode : byte
{
    /// <summary>从 0 开始的 PDU 偏移地址。</summary>
    ZeroBased = 0,

    /// <summary>从 1 开始、未携带寄存器区前缀的地址。</summary>
    OneBased = 1,

    /// <summary>带 0xxxx、1xxxx、3xxxx 或 4xxxx 区域前缀的传统地址。</summary>
    Modicon = 2,
}

/// <summary>
/// 单个 16 位寄存器内的字节顺序。
/// </summary>
public enum ModbusByteOrder : byte
{
    /// <summary>高字节在前。</summary>
    BigEndian = 0,

    /// <summary>低字节在前。</summary>
    LittleEndian = 1,
}

/// <summary>
/// 多寄存器值的字顺序。
/// </summary>
public enum ModbusWordOrder : byte
{
    /// <summary>高位寄存器在前。</summary>
    BigEndian = 0,

    /// <summary>低位寄存器在前。</summary>
    LittleEndian = 1,
}

/// <summary>
/// Modbus 的四个独立地址空间。
/// </summary>
public enum ModbusRegisterArea : byte
{
    /// <summary>线圈，可读写单比特值。</summary>
    Coil = 0,

    /// <summary>离散输入，只读单比特值。</summary>
    DiscreteInput = 1,

    /// <summary>输入寄存器，只读 16 位寄存器。</summary>
    InputRegister = 2,

    /// <summary>保持寄存器，可读写 16 位寄存器。</summary>
    HoldingRegister = 3,
}

/// <summary>
/// Modbus 映射支持的逻辑值类型。
/// </summary>
public enum ModbusValueType : byte
{
    /// <summary>单比特布尔值。</summary>
    Bit = 0,

    /// <summary>16 位有符号整数。</summary>
    Int16 = 1,

    /// <summary>16 位无符号整数。</summary>
    UInt16 = 2,

    /// <summary>32 位有符号整数。</summary>
    Int32 = 3,

    /// <summary>32 位无符号整数。</summary>
    UInt32 = 4,

    /// <summary>IEEE 754 单精度浮点数。</summary>
    Float32 = 5,

    /// <summary>IEEE 754 双精度浮点数。</summary>
    Float64 = 6,

    /// <summary>四位十进制数字的 16 位 BCD 值。</summary>
    Bcd16 = 7,

    /// <summary>八位十进制数字的 32 位 BCD 值。</summary>
    Bcd32 = 8,

    /// <summary>以 ASCII 编码并用 NUL 补齐的定长字符串。</summary>
    String = 9,
}

/// <summary>
/// 列映射允许的访问方向。
/// </summary>
public enum ModbusAccessMode : byte
{
    /// <summary>仅允许读取设备或端点值。</summary>
    Read = 0,

    /// <summary>仅允许写入设备或端点值。</summary>
    Write = 1,

    /// <summary>允许读取和写入。</summary>
    ReadWrite = 2,
}

/// <summary>
/// 表绑定的数据流方向。
/// </summary>
public enum ModbusMappingDirection : byte
{
    /// <summary>从外部 Modbus source 采集到关系表。</summary>
    SourceToTable = 0,

    /// <summary>把关系表中的值暴露到 Modbus endpoint。</summary>
    TableToEndpoint = 1,
}

/// <summary>
/// 表绑定选择当前行还是历史行。
/// </summary>
public enum ModbusTableMode : byte
{
    /// <summary>使用当前最新行。</summary>
    Latest = 0,

    /// <summary>保留并读取历史采样行。</summary>
    History = 1,
}

/// <summary>
/// source 采集失败时的表写入策略。
/// </summary>
public enum ModbusErrorPolicy : byte
{
    /// <summary>保留上一次成功采集的值。</summary>
    KeepLast = 0,

    /// <summary>将失败列写为 NULL。</summary>
    Null = 1,

    /// <summary>跳过本次失败采样。</summary>
    Skip = 2,

    /// <summary>保留值并把质量列标记为异常。</summary>
    MarkBad = 3,
}

/// <summary>
/// endpoint 收到外部写请求时的入口策略。
/// </summary>
public enum ModbusEndpointWritePolicy : byte
{
    /// <summary>拒绝所有外部写请求。</summary>
    Reject = 0,

    /// <summary>写请求只进入待审批队列。</summary>
    Staged = 1,
}

/// <summary>
/// endpoint 写请求获批后的动作。
/// </summary>
public enum ModbusApprovedWriteAction : byte
{
    /// <summary>仅记录审批结果，不更新绑定表。</summary>
    StageOnly = 0,

    /// <summary>审批通过后更新绑定表。</summary>
    UpdateTable = 1,
}

/// <summary>
/// 定义一个主动轮询外部设备的 Modbus TCP source。
/// </summary>
/// <param name="Name">source 的唯一名称。</param>
/// <param name="Host">设备主机名或 IP 地址。</param>
/// <param name="Port">设备 TCP 端口。</param>
/// <param name="UnitId">Modbus 单元标识。</param>
/// <param name="AddressingMode">DDL 地址的解释方式。</param>
/// <param name="PollIntervalMilliseconds">轮询间隔，单位为毫秒。</param>
/// <param name="TimeoutMilliseconds">单次请求超时，单位为毫秒。</param>
/// <param name="RetryCount">单次轮询允许的重试次数。</param>
/// <param name="ByteOrder">默认寄存器内字节序。</param>
/// <param name="WordOrder">默认多寄存器字序。</param>
/// <param name="Enabled">是否启用协议运行时；默认关闭。</param>
public sealed record ModbusSourceDefinition(
    string Name,
    string Host,
    int Port = 502,
    byte UnitId = 1,
    ModbusAddressingMode AddressingMode = ModbusAddressingMode.Modicon,
    int PollIntervalMilliseconds = 1_000,
    int TimeoutMilliseconds = 3_000,
    int RetryCount = 3,
    ModbusByteOrder ByteOrder = ModbusByteOrder.BigEndian,
    ModbusWordOrder WordOrder = ModbusWordOrder.BigEndian,
    bool Enabled = false);

/// <summary>
/// 定义一个供外部主站访问的 Modbus TCP endpoint。
/// </summary>
/// <param name="Name">endpoint 的唯一名称。</param>
/// <param name="BindAddress">监听的本地地址。</param>
/// <param name="Port">监听的 TCP 端口。</param>
/// <param name="UnitId">Modbus 单元标识。</param>
/// <param name="MaxConnections">允许的最大并发连接数。</param>
/// <param name="AllowedClientNetworks">允许访问的客户端 IP 或 CIDR；空集合仅允许用于回环监听。</param>
/// <param name="AddressingMode">DDL 地址的解释方式。</param>
/// <param name="ByteOrder">默认寄存器内字节序。</param>
/// <param name="WordOrder">默认多寄存器字序。</param>
/// <param name="WritePolicy">外部写请求的入口策略。</param>
/// <param name="Enabled">是否启用协议运行时；默认关闭。</param>
public sealed record ModbusEndpointDefinition(
    string Name,
    string BindAddress = "127.0.0.1",
    int Port = 502,
    byte UnitId = 1,
    int MaxConnections = 32,
    IReadOnlyList<string>? AllowedClientNetworks = null,
    ModbusAddressingMode AddressingMode = ModbusAddressingMode.Modicon,
    ModbusByteOrder ByteOrder = ModbusByteOrder.BigEndian,
    ModbusWordOrder WordOrder = ModbusWordOrder.BigEndian,
    ModbusEndpointWritePolicy WritePolicy = ModbusEndpointWritePolicy.Staged,
    bool Enabled = false);

/// <summary>
/// 定义一个关系表列与 Modbus 地址区间之间的映射。
/// </summary>
/// <param name="ColumnName">关系表列名。</param>
/// <param name="Area">Modbus 地址空间。</param>
/// <param name="DeclaredAddress">DDL 中保留的原始声明地址。</param>
/// <param name="PduAddress">从 0 开始的规范化 PDU 地址。</param>
/// <param name="ValueType">映射值类型。</param>
/// <param name="RegisterCount">映射占用的线圈、离散输入或寄存器数量。</param>
/// <param name="StringLength">STRING 的固定 ASCII 字节数；其他类型必须为 0。</param>
/// <param name="BitIndex">寄存器内比特索引；非 BIT 类型必须为 null。</param>
/// <param name="ByteOrder">寄存器内字节序。</param>
/// <param name="WordOrder">多寄存器字序。</param>
/// <param name="Scale">从原始值换算到表值的乘数。</param>
/// <param name="Offset">从原始值换算到表值时增加的偏移量。</param>
/// <param name="Access">允许的访问方向。</param>
public sealed record ModbusColumnMapping(
    string ColumnName,
    ModbusRegisterArea Area,
    int DeclaredAddress,
    ushort PduAddress,
    ModbusValueType ValueType,
    int RegisterCount = 1,
    int StringLength = 0,
    int? BitIndex = null,
    ModbusByteOrder ByteOrder = ModbusByteOrder.BigEndian,
    ModbusWordOrder WordOrder = ModbusWordOrder.BigEndian,
    decimal Scale = 1m,
    decimal Offset = 0m,
    ModbusAccessMode Access = ModbusAccessMode.Read);

/// <summary>
/// 定义关系表与一个 Modbus source 或 endpoint 的完整绑定。
/// </summary>
/// <param name="TableName">关系表名称。</param>
/// <param name="Direction">数据流方向。</param>
/// <param name="TargetName">source 或 endpoint 名称。</param>
/// <param name="Columns">列映射集合。</param>
/// <param name="RowKey">LATEST 模式使用的固定 Int64 主键；null 表示由运行时解析。</param>
/// <param name="TableMode">当前行或历史行模式。</param>
/// <param name="ErrorPolicy">采集失败时的处理策略。</param>
/// <param name="ApprovedWriteAction">endpoint 写请求获批后的动作。</param>
/// <param name="StoreHistory">是否为每次成功采样保留历史行。</param>
/// <param name="SampleTimeColumn">保存采样时间的列名。</param>
/// <param name="QualityColumn">保存采样质量状态的列名。</param>
/// <param name="Enabled">是否启用该绑定；默认关闭。</param>
public sealed record ModbusTableBinding(
    string TableName,
    ModbusMappingDirection Direction,
    string TargetName,
    IReadOnlyList<ModbusColumnMapping> Columns,
    long? RowKey = null,
    ModbusTableMode TableMode = ModbusTableMode.Latest,
    ModbusErrorPolicy ErrorPolicy = ModbusErrorPolicy.KeepLast,
    ModbusApprovedWriteAction ApprovedWriteAction = ModbusApprovedWriteAction.StageOnly,
    bool StoreHistory = false,
    string? SampleTimeColumn = null,
    string? QualityColumn = null,
    bool Enabled = false);
