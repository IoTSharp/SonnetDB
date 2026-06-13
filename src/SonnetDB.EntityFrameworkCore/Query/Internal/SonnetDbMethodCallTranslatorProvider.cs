using Microsoft.EntityFrameworkCore.Query;

namespace SonnetDB.EntityFrameworkCore.Query.Internal;

/// <summary>
/// SonnetDB method-call translator provider.
/// </summary>
public sealed class SonnetDbMethodCallTranslatorProvider : RelationalMethodCallTranslatorProvider
{
    /// <summary>
    /// Creates the SonnetDB method-call translator provider.
    /// </summary>
    /// <param name="dependencies">Relational translator dependencies.</param>
    public SonnetDbMethodCallTranslatorProvider(RelationalMethodCallTranslatorProviderDependencies dependencies)
        : base(dependencies)
    {
        AddTranslators([new SonnetDbStringMethodTranslator(dependencies.SqlExpressionFactory)]);
    }
}
