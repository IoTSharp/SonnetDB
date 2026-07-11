using SonnetDB.LanguageServer;
using Xunit;

namespace SonnetDB.LanguageServer.Tests;

public sealed class SqlValidationServiceTests
{
    [Fact]
    public void Validate_ValidScript_ReturnsNoDiagnostics()
    {
        var diagnostics = SqlValidationService.Validate("SELECT * FROM cpu LIMIT 10;");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Validate_InvalidCharacter_ReturnsParserPosition()
    {
        const string sql = "SELECT * FROM cpu @";

        var diagnostic = Assert.Single(SqlValidationService.Validate(sql));

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(sql.IndexOf('@'), diagnostic.Offset);
        Assert.Equal(1, diagnostic.Length);
        Assert.Contains("无法识别", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_UnexpectedEnd_ReturnsZeroLengthDiagnosticAtEnd()
    {
        const string sql = "SELECT * FROM";

        var diagnostic = Assert.Single(SqlValidationService.Validate(sql));

        Assert.Equal(sql.Length, diagnostic.Offset);
        Assert.Equal(0, diagnostic.Length);
    }
}
