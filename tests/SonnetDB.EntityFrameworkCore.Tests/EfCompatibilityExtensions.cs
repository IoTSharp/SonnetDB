using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace SonnetDB.EntityFrameworkCore.Tests;

internal static class EfCompatibilityExtensions
{
    public static Task<int> ExecuteSqlCompatAsync(
        this DatabaseFacade database,
        FormattableString sql,
        CancellationToken cancellationToken = default)
    {
#if NET11_0_OR_GREATER
        return database.ExecuteSqlAsync(sql, cancellationToken);
#else
        return database.ExecuteSqlInterpolatedAsync(sql, cancellationToken);
#endif
    }
}
