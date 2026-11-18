using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

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

    public static NotSupportedException AssertNotSupported(Action action)
    {
        var exception = Assert.ThrowsAny<Exception>(action);
        return Assert.IsType<NotSupportedException>(Unwrap(exception));
    }

    public static async Task<NotSupportedException> AssertNotSupportedAsync(Func<Task> action)
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(action);
        return Assert.IsType<NotSupportedException>(Unwrap(exception));
    }

    private static Exception Unwrap(Exception exception)
    {
#if NET11_0_OR_GREATER
        Assert.IsType<InvalidOperationException>(exception);
#else
        Assert.IsType<NotSupportedException>(exception);
#endif
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is NotSupportedException unsupported)
                return unsupported;
        return exception;
    }
}
