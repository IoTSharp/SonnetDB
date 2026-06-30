using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace SonnetDB.EntityFrameworkCore.ValueGeneration.Internal;

/// <summary>
/// Provides client-side value generation for types SonnetDB does not generate on the server.
/// </summary>
public sealed class SonnetDbValueGeneratorSelector : ValueGeneratorSelector
{
    private readonly GuidValueGenerator _guid = new();

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
        => clrType == typeof(Guid) && property.IsPrimaryKey()
            ? _guid
            : base.FindForType(property, typeBase, clrType);
}
