using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using SonnetDB.EntityFrameworkCore.Storage.Internal;

namespace SonnetDB.EntityFrameworkCore.Update.Internal;

/// <summary>复用 EF 修改命令，为需要 JSON 编码的属性保留完整序列化片段。</summary>
public sealed class SonnetDbModificationCommandFactory : IModificationCommandFactory
{
    /// <inheritdoc />
    public IModificationCommand CreateModificationCommand(in ModificationCommandParameters modificationCommandParameters)
        => new JsonModificationCommand(modificationCommandParameters);

    /// <inheritdoc />
    public INonTrackedModificationCommand CreateNonTrackedModificationCommand(in NonTrackedModificationCommandParameters modificationCommandParameters)
        => new ModificationCommand(modificationCommandParameters);

    private static bool UsesNativeScalar(IProperty property)
    {
        var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        return property.GetRelationalTypeMapping().Converter is null
            && (type == typeof(string) || type == typeof(bool) || type == typeof(byte) || type == typeof(short)
                || type == typeof(int) || type == typeof(long) || type == typeof(float) || type == typeof(double));
    }

    private sealed class JsonModificationCommand(in ModificationCommandParameters parameters) : ModificationCommand(parameters)
    {
        protected override void ProcessSinglePropertyJsonUpdate(ref ColumnModificationParameters parameters)
        {
            if (parameters.Property is { } property && !UsesNativeScalar(property))
            {
                var readerWriter = property.GetJsonValueReaderWriter() ?? property.GetTypeMapping().JsonValueReaderWriter
                    ?? throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
                parameters = parameters with
                {
                    Value = parameters.Value is null ? null : readerWriter.ToJsonString(parameters.Value),
                    TypeMapping = new SonnetDbJsonTypeMapping(),
                };
                return;
            }

            base.ProcessSinglePropertyJsonUpdate(ref parameters);
        }
    }
}
