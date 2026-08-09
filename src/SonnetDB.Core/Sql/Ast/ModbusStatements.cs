using SonnetDB.Modbus;

namespace SonnetDB.Sql.Ast;

/// <summary>
/// <c>CREATE MODBUS SOURCE</c>：创建主站轮询源定义。
/// </summary>
/// <param name="Definition">待持久化的 Modbus source 定义。</param>
public sealed record CreateModbusSourceStatement(
    ModbusSourceDefinition Definition) : SqlStatement
{
    /// <summary>Source 名称。</summary>
    public string Name => Definition.Name;
}

/// <summary>
/// <c>CREATE MODBUS ENDPOINT</c>：创建从站监听端点定义。
/// </summary>
/// <param name="Definition">待持久化的 Modbus endpoint 定义。</param>
public sealed record CreateModbusEndpointStatement(
    ModbusEndpointDefinition Definition) : SqlStatement
{
    /// <summary>Endpoint 名称。</summary>
    public string Name => Definition.Name;
}

/// <summary>
/// 描述 CREATE TABLE 中尚未解析目标对象默认值的 Modbus 列映射语法。
/// </summary>
/// <param name="Direction">列值的数据流方向。</param>
/// <param name="Area">声明的 Modbus 地址空间。</param>
/// <param name="DeclaredAddress">DDL 中保留的原始地址。</param>
/// <param name="RegisterCount">由 wire type 推导并校验后的地址数量。</param>
/// <param name="BitIndex">可选的寄存器内 bit 索引。</param>
/// <param name="ValueType">线上 wire type。</param>
/// <param name="StringLength">STRING 的固定 ASCII 字节数；其他类型为 0。</param>
/// <param name="ByteOrderOverride">可选的列级寄存器内字节序覆盖。</param>
/// <param name="WordOrderOverride">可选的列级多寄存器字序覆盖。</param>
/// <param name="Scale">读取原始值时使用的缩放倍数。</param>
/// <param name="Offset">读取原始值时增加的偏移量。</param>
/// <param name="Access">映射访问模式。</param>
public sealed record ModbusColumnMappingClause(
    ModbusMappingDirection Direction,
    ModbusRegisterArea Area,
    int DeclaredAddress,
    int RegisterCount,
    int? BitIndex,
    ModbusValueType ValueType,
    int StringLength,
    ModbusByteOrder? ByteOrderOverride,
    ModbusWordOrder? WordOrderOverride,
    decimal Scale,
    decimal Offset,
    ModbusAccessMode Access);

/// <summary>
/// 描述 CREATE TABLE 尾部尚未解析目标 catalog 的 USING MODBUS 绑定语法。
/// </summary>
/// <param name="Direction">表与 source 或 endpoint 之间的数据流方向。</param>
/// <param name="TargetName">绑定的 source 或 endpoint 名称。</param>
/// <param name="TableMode">source 表的 latest/history 模式。</param>
/// <param name="ErrorPolicy">source 采集失败策略。</param>
/// <param name="StoreHistory">是否额外保存历史采样。</param>
/// <param name="RowKey">endpoint 暴露的固定关系表主键。</param>
/// <param name="ApprovedWriteAction">endpoint 请求获批后的应用动作。</param>
public sealed record ModbusTableBindingClause(
    ModbusMappingDirection Direction,
    string TargetName,
    ModbusTableMode TableMode = ModbusTableMode.Latest,
    ModbusErrorPolicy ErrorPolicy = ModbusErrorPolicy.KeepLast,
    bool StoreHistory = false,
    long? RowKey = null,
    ModbusApprovedWriteAction ApprovedWriteAction = ModbusApprovedWriteAction.StageOnly);

/// <summary><c>SHOW MODBUS SOURCES</c>：列出当前数据库的 Modbus source。</summary>
public sealed record ShowModbusSourcesStatement : SqlStatement;

/// <summary><c>SHOW MODBUS ENDPOINTS</c>：列出当前数据库的 Modbus endpoint。</summary>
public sealed record ShowModbusEndpointsStatement : SqlStatement;

/// <summary><c>DESCRIBE MODBUS SOURCE name</c>：描述指定 Modbus source。</summary>
/// <param name="Name">Source 名称。</param>
public sealed record DescribeModbusSourceStatement(string Name) : SqlStatement;

/// <summary><c>DESCRIBE MODBUS ENDPOINT name</c>：描述指定 Modbus endpoint。</summary>
/// <param name="Name">Endpoint 名称。</param>
public sealed record DescribeModbusEndpointStatement(string Name) : SqlStatement;

/// <summary><c>DESCRIBE MODBUS TABLE name</c>：描述指定表的 Modbus 映射。</summary>
/// <param name="Name">关系表名称。</param>
public sealed record DescribeModbusTableStatement(string Name) : SqlStatement;

/// <summary>
/// 受限 Modbus source 写入的执行阶段。
/// </summary>
public enum ModbusWriteMode
{
    /// <summary>只执行目录、当前行与编码校验，不签发确认令牌。</summary>
    DryRun = 0,

    /// <summary>执行完整预览并签发一次性确认令牌。</summary>
    Preview = 1,

    /// <summary>消费一次性确认令牌并执行远端写入。</summary>
    Confirm = 2,
}

/// <summary>
/// <c>WRITE MODBUS table SET column = value DRY RUN|PREVIEW|CONFIRM token</c>：
/// 对单个 LATEST source 映射列执行受治理的远端写入。
/// </summary>
/// <param name="TableName">唯一目标关系表。</param>
/// <param name="ColumnName">唯一目标 Modbus 映射列。</param>
/// <param name="Value">待编码的逻辑值表达式；执行前必须绑定为字面量。</param>
/// <param name="Mode">dry-run、preview 或确认执行阶段。</param>
/// <param name="ConfirmationToken">确认阶段的一次性令牌表达式；其他阶段为 <c>null</c>。</param>
public sealed record WriteModbusStatement(
    string TableName,
    string ColumnName,
    SqlExpression Value,
    ModbusWriteMode Mode,
    SqlExpression? ConfirmationToken = null) : SqlStatement;

/// <summary><c>SHOW MODBUS WRITE AUDIT</c>：列出持久化的受限远端写审计。</summary>
public sealed record ShowModbusWriteAuditStatement : SqlStatement;
