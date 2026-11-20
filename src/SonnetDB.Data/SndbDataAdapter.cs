using System.Data.Common;

namespace SonnetDB.Data;

/// <summary>
/// SonnetDB 的 ADO.NET <see cref="DbDataAdapter"/> 入口。
/// </summary>
/// <remarks>
/// <para>
/// 适配器复用 BCL 的 <see cref="DbDataAdapter"/> 实现，支持使用
/// <see cref="System.Data.DataTable"/> 或 <see cref="System.Data.DataSet"/> 的
/// <c>Fill</c>、<c>FillSchema</c> 和显式配置命令后的 <c>Update</c>。
/// </para>
/// <para>
/// 适配器不会自动生成 INSERT、UPDATE 和 DELETE 命令。需要更新已填充的数据时，
/// 调用方必须设置 <see cref="DbDataAdapter.InsertCommand"/>、
/// <see cref="DbDataAdapter.UpdateCommand"/> 和
/// <see cref="DbDataAdapter.DeleteCommand"/>，并为每条命令配置与列映射相符的参数。
/// </para>
/// <para>
/// 适配器不拥有调用方传入的命令或连接。调用方应在适配器和命令的生命周期结束时分别释放它们。
/// </para>
/// </remarks>
public sealed class SndbDataAdapter : DbDataAdapter
{
    /// <summary>创建未配置命令的适配器。</summary>
    public SndbDataAdapter()
    {
    }

    /// <summary>使用 SELECT 命令创建适配器。</summary>
    /// <param name="selectCommand">用于填充 DataTable 或 DataSet 的命令。</param>
    public SndbDataAdapter(SndbCommand selectCommand)
    {
        ArgumentNullException.ThrowIfNull(selectCommand);
        SelectCommand = selectCommand;
    }

    /// <summary>使用 SELECT 命令文本和连接创建适配器。</summary>
    /// <param name="selectCommandText">用于填充 DataTable 或 DataSet 的 SQL。</param>
    /// <param name="connection">执行 SELECT 命令的 SonnetDB 连接。</param>
    public SndbDataAdapter(string selectCommandText, SndbConnection connection)
        : this(CreateSelectCommand(selectCommandText, connection))
    {
    }

    private static SndbCommand CreateSelectCommand(string selectCommandText, SndbConnection connection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectCommandText);
        ArgumentNullException.ThrowIfNull(connection);

        return new SndbCommand(selectCommandText, connection);
    }
}
