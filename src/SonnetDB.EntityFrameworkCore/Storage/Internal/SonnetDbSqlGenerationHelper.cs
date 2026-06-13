using System.Text;
using Microsoft.EntityFrameworkCore.Storage;

namespace SonnetDB.EntityFrameworkCore.Storage.Internal;

/// <summary>
/// SonnetDB SQL 方言的标识符与参数名生成器。
/// </summary>
public sealed class SonnetDbSqlGenerationHelper : RelationalSqlGenerationHelper
{
    /// <summary>
    /// 创建 SonnetDB SQL 生成帮助器。
    /// </summary>
    /// <param name="dependencies">SQL 生成依赖。</param>
    public SonnetDbSqlGenerationHelper(RelationalSqlGenerationHelperDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc />
    public override string EscapeIdentifier(string identifier)
        => identifier.Replace("\"", "\"\"", StringComparison.Ordinal);

    /// <inheritdoc />
    public override void EscapeIdentifier(StringBuilder builder, string identifier)
        => builder.Append(EscapeIdentifier(identifier));

    /// <inheritdoc />
    public override string DelimitIdentifier(string identifier)
        => "\"" + EscapeIdentifier(identifier) + "\"";

    /// <inheritdoc />
    public override void DelimitIdentifier(StringBuilder builder, string identifier)
        => builder.Append('"').Append(EscapeIdentifier(identifier)).Append('"');

    /// <inheritdoc />
    public override string GenerateParameterName(string name)
        => "@" + name;

    /// <inheritdoc />
    public override void GenerateParameterName(StringBuilder builder, string name)
        => builder.Append('@').Append(name);

    /// <inheritdoc />
    public override string GenerateParameterNamePlaceholder(string name)
        => "@" + name;

    /// <inheritdoc />
    public override void GenerateParameterNamePlaceholder(StringBuilder builder, string name)
        => builder.Append('@').Append(name);
}
