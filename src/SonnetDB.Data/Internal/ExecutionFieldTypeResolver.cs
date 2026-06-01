using System.Diagnostics.CodeAnalysis;

using SonnetDB.Model;

namespace SonnetDB.Data.Internal;

internal enum ExecutionFieldTypeKind
{
    Object,
    String,
    Int64,
    Int32,
    Int16,
    Byte,
    Double,
    Single,
    Decimal,
    Boolean,
    DateTime,
    DateTimeOffset,
    Guid,
    GeoPoint,
    ByteArray,
}

internal static class ExecutionFieldTypeResolver
{
    public static ExecutionFieldTypeKind Resolve(object? value)
    {
        return value switch
        {
            null or DBNull => ExecutionFieldTypeKind.Object,
            string => ExecutionFieldTypeKind.String,
            long => ExecutionFieldTypeKind.Int64,
            int => ExecutionFieldTypeKind.Int32,
            short => ExecutionFieldTypeKind.Int16,
            byte => ExecutionFieldTypeKind.Byte,
            double => ExecutionFieldTypeKind.Double,
            float => ExecutionFieldTypeKind.Single,
            decimal => ExecutionFieldTypeKind.Decimal,
            bool => ExecutionFieldTypeKind.Boolean,
            DateTime => ExecutionFieldTypeKind.DateTime,
            DateTimeOffset => ExecutionFieldTypeKind.DateTimeOffset,
            Guid => ExecutionFieldTypeKind.Guid,
            GeoPoint => ExecutionFieldTypeKind.GeoPoint,
            byte[] => ExecutionFieldTypeKind.ByteArray,
            _ => ExecutionFieldTypeKind.Object,
        };
    }

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
    public static Type GetRuntimeType(ExecutionFieldTypeKind kind)
    {
        return kind switch
        {
            ExecutionFieldTypeKind.String => typeof(string),
            ExecutionFieldTypeKind.Int64 => typeof(long),
            ExecutionFieldTypeKind.Int32 => typeof(int),
            ExecutionFieldTypeKind.Int16 => typeof(short),
            ExecutionFieldTypeKind.Byte => typeof(byte),
            ExecutionFieldTypeKind.Double => typeof(double),
            ExecutionFieldTypeKind.Single => typeof(float),
            ExecutionFieldTypeKind.Decimal => typeof(decimal),
            ExecutionFieldTypeKind.Boolean => typeof(bool),
            ExecutionFieldTypeKind.DateTime => typeof(DateTime),
            ExecutionFieldTypeKind.DateTimeOffset => typeof(DateTimeOffset),
            ExecutionFieldTypeKind.Guid => typeof(Guid),
            ExecutionFieldTypeKind.GeoPoint => typeof(GeoPoint),
            ExecutionFieldTypeKind.ByteArray => typeof(byte[]),
            _ => typeof(object),
        };
    }
}
