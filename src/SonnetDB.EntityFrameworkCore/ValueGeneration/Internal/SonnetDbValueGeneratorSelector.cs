using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace SonnetDB.EntityFrameworkCore.ValueGeneration.Internal;

/// <summary>
/// Provides client-side value generation for types SonnetDB does not generate on the server.
/// </summary>
public sealed class SonnetDbValueGeneratorSelector : ValueGeneratorSelector
{
    private readonly GuidValueGenerator _guid = new();
    private readonly SonnetDbIntValueGenerator _int = new();
    private readonly SonnetDbLongValueGenerator _long = new();

    /// <summary>
    /// Creates a SonnetDB value generator selector.
    /// </summary>
    /// <param name="dependencies">EF Core value generator selector dependencies.</param>
    public SonnetDbValueGeneratorSelector(ValueGeneratorSelectorDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc />
    protected override ValueGenerator? FindForType(IProperty property, ITypeBase typeBase, Type clrType)
    {
        if (!property.IsPrimaryKey() || property.ValueGenerated != ValueGenerated.OnAdd)
        {
            return base.FindForType(property, typeBase, clrType);
        }

        if (clrType == typeof(Guid))
        {
            return _guid;
        }

        if (clrType == typeof(int))
        {
            return _int;
        }

        if (clrType == typeof(long))
        {
            return _long;
        }

        return base.FindForType(property, typeBase, clrType);
    }

    private sealed class SonnetDbIntValueGenerator : ValueGenerator<int>
    {
        private int _current = Math.Abs(Environment.TickCount % 1_000_000_000);

        public override bool GeneratesTemporaryValues => false;

        public override int Next(EntityEntry entry)
            => Interlocked.Increment(ref _current);
    }

    private sealed class SonnetDbLongValueGenerator : ValueGenerator<long>
    {
        private long _current = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public override bool GeneratesTemporaryValues => false;

        public override long Next(EntityEntry entry)
            => Interlocked.Increment(ref _current);
    }
}
