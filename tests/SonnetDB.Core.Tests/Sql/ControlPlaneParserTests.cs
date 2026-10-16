using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// PR #34a-3：控制面 DDL（CREATE/DROP/ALTER USER、GRANT/REVOKE、CREATE/DROP DATABASE）解析测试。
/// </summary>
public sealed class ControlPlaneParserTests
{
    [Fact]
    public void Parse_CreateUser_BasicForm()
    {
        var stmt = (CreateUserStatement)SqlParser.Parse("CREATE USER alice WITH PASSWORD 'pa$$'");
        Assert.Equal("alice", stmt.UserName);
        Assert.Equal("pa$$", stmt.Password);
        Assert.False(stmt.IsSuperuser);
    }

    [Fact]
    public void Parse_CreateUser_PreservesIdentifierCase()
    {
        var stmt = (CreateUserStatement)SqlParser.Parse("create user Alice with password 'p'");
        Assert.Equal("Alice", stmt.UserName);
    }

    [Fact]
    public void Parse_EmailUser_ControlPlaneStatementsUseQuotedIdentifiers()
    {
        var create = Assert.IsType<CreateUserStatement>(SqlParser.Parse("CREATE USER \"demo@example.test\" WITH PASSWORD 'example-only'"));
        var alter = Assert.IsType<AlterUserPasswordStatement>(SqlParser.Parse("ALTER USER \"demo@example.test\" WITH PASSWORD 'replacement-only'"));
        var grant = Assert.IsType<GrantStatement>(SqlParser.Parse("GRANT WRITE ON DATABASE demo TO \"demo@example.test\""));
        Assert.Equal("demo@example.test", create.UserName);
        Assert.Equal(create.UserName, alter.UserName);
        Assert.Equal(create.UserName, grant.UserName);
    }

    [Fact]
    public void Parse_CreateUser_WithEscapedQuoteInPassword()
    {
        var stmt = (CreateUserStatement)SqlParser.Parse("CREATE USER bob WITH PASSWORD 'O''Hara'");
        Assert.Equal("O'Hara", stmt.Password);
    }

    [Fact]
    public void Parse_CreateUser_WithSuperuserKeyword_SetsFlag()
    {
        var stmt = (CreateUserStatement)SqlParser.Parse("CREATE USER admin2 WITH PASSWORD 'p' SUPERUSER");
        Assert.Equal("admin2", stmt.UserName);
        Assert.True(stmt.IsSuperuser);
    }

    [Fact]
    public void Parse_CreateUser_SuperuserKeyword_CaseInsensitive()
    {
        var stmt = (CreateUserStatement)SqlParser.Parse("create user u with password 'p' superuser");
        Assert.True(stmt.IsSuperuser);
    }

    [Theory]
    [InlineData("CREATE USER")]
    [InlineData("CREATE USER alice")]
    [InlineData("CREATE USER alice WITH")]
    [InlineData("CREATE USER alice WITH PASSWORD")]
    [InlineData("CREATE USER alice WITH PASSWORD 123")]
    [InlineData("CREATE USER 'alice' WITH PASSWORD 'p'")]
    public void Parse_CreateUser_BadGrammar_Throws(string sql)
    {
        Assert.Throws<SqlParseException>(() => SqlParser.Parse(sql));
    }

    [Fact]
    public void Parse_AlterUser_PasswordChange()
    {
        var stmt = (AlterUserPasswordStatement)SqlParser.Parse("ALTER USER alice WITH PASSWORD 'new'");
        Assert.Equal("alice", stmt.UserName);
        Assert.Equal("new", stmt.NewPassword);
    }

    [Fact]
    public void Parse_DropUser()
    {
        var stmt = (DropUserStatement)SqlParser.Parse("DROP USER alice");
        Assert.Equal("alice", stmt.UserName);
    }

    [Fact]
    public void Parse_DropDatabase()
    {
        var stmt = (DropDatabaseStatement)SqlParser.Parse("DROP DATABASE metrics");
        Assert.Equal("metrics", stmt.DatabaseName);
    }

    [Fact]
    public void Parse_Drop_WithoutKind_Throws()
    {
        Assert.Throws<SqlParseException>(() => SqlParser.Parse("DROP alice"));
    }

    [Fact]
    public void Parse_CreateDatabase()
    {
        var stmt = (CreateDatabaseStatement)SqlParser.Parse("CREATE DATABASE metrics");
        Assert.Equal("metrics", stmt.DatabaseName);
    }

    [Theory]
    [InlineData("GRANT READ ON DATABASE metrics TO alice", GrantPermission.Read, "metrics", "alice")]
    [InlineData("GRANT WRITE ON DATABASE metrics TO bob", GrantPermission.Write, "metrics", "bob")]
    [InlineData("GRANT ADMIN ON DATABASE metrics TO carol", GrantPermission.Admin, "metrics", "carol")]
    [InlineData("grant read on database metrics to alice", GrantPermission.Read, "metrics", "alice")]
    public void Parse_Grant_Variations(string sql, GrantPermission perm, string db, string user)
    {
        var stmt = (GrantStatement)SqlParser.Parse(sql);
        Assert.Equal(perm, stmt.Permission);
        Assert.Equal(db, stmt.Database);
        Assert.Equal(user, stmt.UserName);
    }

    [Fact]
    public void Parse_Grant_StarDatabase_AcceptsWildcard()
    {
        var stmt = (GrantStatement)SqlParser.Parse("GRANT READ ON DATABASE * TO alice");
        Assert.Equal("*", stmt.Database);
    }

    [Theory]
    [InlineData("GRANT FOO ON DATABASE m TO a")]
    [InlineData("GRANT READ DATABASE m TO a")]
    [InlineData("GRANT READ ON m TO a")]
    [InlineData("GRANT READ ON DATABASE m a")]
    public void Parse_Grant_BadGrammar_Throws(string sql)
    {
        Assert.Throws<SqlParseException>(() => SqlParser.Parse(sql));
    }

    [Fact]
    public void Parse_Revoke_FromUser()
    {
        var stmt = (RevokeStatement)SqlParser.Parse("REVOKE ON DATABASE metrics FROM alice");
        Assert.Equal("metrics", stmt.Database);
        Assert.Equal("alice", stmt.UserName);
    }

    [Fact]
    public void Parse_Revoke_StarDatabase()
    {
        var stmt = (RevokeStatement)SqlParser.Parse("REVOKE ON DATABASE * FROM alice");
        Assert.Equal("*", stmt.Database);
    }

    [Fact]
    public void Parse_Script_MixesControlPlaneAndDml()
    {
        var stmts = SqlParser.ParseScript(
            "CREATE USER alice WITH PASSWORD 'p';" +
            "GRANT WRITE ON DATABASE metrics TO alice;" +
            "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT);" +
            "DROP USER alice");
        Assert.Equal(4, stmts.Count);
        Assert.IsType<CreateUserStatement>(stmts[0]);
        Assert.IsType<GrantStatement>(stmts[1]);
        Assert.IsType<CreateMeasurementStatement>(stmts[2]);
        Assert.IsType<DropUserStatement>(stmts[3]);
    }

    [Fact]
    public void Parse_Script_SkipsRemCommentAfterStatement()
    {
        var stmts = SqlParser.ParseScript(
            "CREATE DATABASE demo;\n" +
            "REM cleanup is manual\n" +
            "SHOW DATABASES;");

        Assert.Equal(2, stmts.Count);
        Assert.IsType<CreateDatabaseStatement>(stmts[0]);
        Assert.IsType<ShowDatabasesStatement>(stmts[1]);
    }

    [Fact]
    public void Parse_ShowUsers()
    {
        var stmt = SqlParser.Parse("SHOW USERS");
        Assert.IsType<ShowUsersStatement>(stmt);
    }

    [Fact]
    public void Parse_ShowDatabases()
    {
        var stmt = SqlParser.Parse("show databases");
        Assert.IsType<ShowDatabasesStatement>(stmt);
    }

    [Fact]
    public void Parse_ShowGrants_NoFilter()
    {
        var stmt = (ShowGrantsStatement)SqlParser.Parse("SHOW GRANTS");
        Assert.Null(stmt.UserName);
    }

    [Fact]
    public void Parse_ShowGrants_WithFor()
    {
        var stmt = (ShowGrantsStatement)SqlParser.Parse("SHOW GRANTS FOR alice");
        Assert.Equal("alice", stmt.UserName);
    }

    [Theory]
    [InlineData("SHOW")]
    [InlineData("SHOW FOO")]
    [InlineData("SHOW GRANTS FOR")]
    public void Parse_Show_BadGrammar_Throws(string sql)
    {
        Assert.Throws<SqlParseException>(() => SqlParser.Parse(sql));
    }

    // PR #34b-3-tokens：API token 管理 SQL。

    [Fact]
    public void Parse_ShowTokens_All()
    {
        var stmt = (ShowTokensStatement)SqlParser.Parse("SHOW TOKENS");
        Assert.Null(stmt.UserName);
    }

    [Fact]
    public void Parse_ShowTokens_ForUser()
    {
        var stmt = (ShowTokensStatement)SqlParser.Parse("SHOW TOKENS FOR alice");
        Assert.Equal("alice", stmt.UserName);
    }

    [Fact]
    public void Parse_ShowTokens_ForQuotedUser()
    {
        var stmt = (ShowTokensStatement)SqlParser.Parse("SHOW TOKENS FOR 'ops-admin'");
        Assert.Equal("ops-admin", stmt.UserName);
    }

    [Fact]
    public void Parse_IssueToken_ForUser()
    {
        var stmt = (IssueTokenStatement)SqlParser.Parse("ISSUE TOKEN FOR alice");
        Assert.Equal("alice", stmt.UserName);
    }

    [Fact]
    public void Parse_IssueToken_ForQuotedUser()
    {
        var stmt = (IssueTokenStatement)SqlParser.Parse("ISSUE TOKEN FOR 'ops-admin'");
        Assert.Equal("ops-admin", stmt.UserName);
    }

    [Fact]
    public void Parse_IssueToken_CaseInsensitive()
    {
        var stmt = (IssueTokenStatement)SqlParser.Parse("issue token for Bob");
        Assert.Equal("Bob", stmt.UserName);
    }

    [Fact]
    public void Parse_RevokeToken_ById()
    {
        var stmt = (RevokeTokenStatement)SqlParser.Parse("REVOKE TOKEN 'tok_abcdef'");
        Assert.Equal("tok_abcdef", stmt.TokenId);
    }

    [Fact]
    public void Parse_RevokeOnDatabase_StillWorks()
    {
        // 防回归：REVOKE ON DATABASE ... FROM ... 仍然解析为 RevokeStatement。
        var stmt = (RevokeStatement)SqlParser.Parse("REVOKE ON DATABASE db1 FROM alice");
        Assert.Equal("db1", stmt.Database);
        Assert.Equal("alice", stmt.UserName);
    }

    [Fact]
    public void Parse_ShowGrantsAndRevoke_WithQuotedUser()
    {
        var show = (ShowGrantsStatement)SqlParser.Parse("SHOW GRANTS FOR 'ops-admin'");
        Assert.Equal("ops-admin", show.UserName);

        var revoke = (RevokeStatement)SqlParser.Parse("REVOKE ON DATABASE db1 FROM 'ops-admin'");
        Assert.Equal("db1", revoke.Database);
        Assert.Equal("ops-admin", revoke.UserName);
    }

    [Theory]
    [InlineData("SHOW TOKENS FOR")]
    [InlineData("ISSUE")]
    [InlineData("ISSUE TOKEN")]
    [InlineData("ISSUE TOKEN FOR")]
    [InlineData("REVOKE TOKEN")]
    [InlineData("REVOKE TOKEN tok_abc")]
    public void Parse_TokenStatements_BadGrammar_Throws(string sql)
    {
        Assert.Throws<SqlParseException>(() => SqlParser.Parse(sql));
    }
}
