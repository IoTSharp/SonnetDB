using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace SonnetDB.EntityFrameworkCore.Metadata.Internal;

internal sealed class SonnetDbAnnotationProvider : RelationalAnnotationProvider
{
    public SonnetDbAnnotationProvider(RelationalAnnotationProviderDependencies dependencies)
        : base(dependencies)
    {
    }

    public override IEnumerable<IAnnotation> For(IColumn column, bool designTime)
    {
        if (!designTime || column.PropertyMappings.Count == 0)
            yield break;

        IProperty property = column.PropertyMappings.First().Property;
        Type clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        bool isInteger = clrType == typeof(byte)
            || clrType == typeof(short)
            || clrType == typeof(int)
            || clrType == typeof(long);
        bool hasStoreDefault = property.FindAnnotation(RelationalAnnotationNames.DefaultValue) is not null
            || property.GetDefaultValueSql() is not null
            || property.GetComputedColumnSql() is not null;

        if (property.ValueGenerated == ValueGenerated.OnAdd
            && isInteger
            && !hasStoreDefault
            && string.Equals(column.StoreType, "INT", StringComparison.OrdinalIgnoreCase))
        {
            yield return new Annotation(SonnetDbAnnotationNames.AutoIncrement, true);
        }
    }
}
