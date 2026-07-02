using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Update;

namespace SonnetDB.EntityFrameworkCore.Migrations.Internal;

/// <summary>
/// SonnetDB 迁移 SQL 生成器。
/// </summary>
public sealed class SonnetDbMigrationsSqlGenerator : MigrationsSqlGenerator
{
    /// <summary>
    /// 创建 SonnetDB 迁移 SQL 生成器。
    /// </summary>
    /// <param name="dependencies">迁移 SQL 生成器依赖。</param>
    public SonnetDbMigrationsSqlGenerator(MigrationsSqlGeneratorDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc />
    protected override void Generate(
        CreateTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        builder.Append("CREATE TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" (");

        using (builder.Indent())
        {
            for (var i = 0; i < operation.Columns.Count; i++)
            {
                if (i > 0)
                {
                    builder.AppendLine(",");
                }

                ColumnDefinition(operation.Columns[i], model, builder);
            }

            if (operation.PrimaryKey is not null)
            {
                builder.AppendLine(",");
                PrimaryKeyConstraint(operation.PrimaryKey, model, builder);
            }

            foreach (var foreignKey in operation.ForeignKeys)
            {
                builder.AppendLine(",");
                ForeignKeyConstraint(foreignKey, model, builder);
            }
        }

        builder.AppendLine().Append(")");

        if (terminate)
        {
            builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
            EndStatement(builder);
        }
    }

    /// <inheritdoc />
    protected override void Generate(
        DropTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        builder.Append("DROP TABLE IF EXISTS ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name));

        if (terminate)
        {
            builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
            EndStatement(builder);
        }
    }

    /// <inheritdoc />
    protected override void Generate(
        AddColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        builder.Append("ALTER TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table))
            .Append(" ADD COLUMN ");
        ColumnDefinition(operation, model, builder);

        if (terminate)
        {
            builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
            EndStatement(builder);
        }
    }

    /// <inheritdoc />
    protected override void Generate(
        DropColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        builder.Append("ALTER TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table))
            .Append(" DROP COLUMN IF EXISTS ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name));

        if (terminate)
        {
            builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
            EndStatement(builder);
        }
    }

    /// <inheritdoc />
    protected override void Generate(
        RenameColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        builder.Append("ALTER TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table))
            .Append(" RENAME COLUMN ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" TO ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.NewName!));
        builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
        EndStatement(builder);
    }

    /// <inheritdoc />
    protected override void Generate(
        RenameTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.NewName);

        builder.Append("ALTER TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" RENAME TO ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.NewName));
        builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
        EndStatement(builder);
    }

    /// <inheritdoc />
    protected override void Generate(
        CreateIndexOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        builder.Append("CREATE ");
        if (operation.IsUnique)
        {
            builder.Append("UNIQUE ");
        }

        builder.Append("INDEX IF NOT EXISTS ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" ON ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table))
            .Append(" (")
            .Append(string.Join(", ", operation.Columns.Select(Dependencies.SqlGenerationHelper.DelimitIdentifier)))
            .Append(")");

        if (terminate)
        {
            builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
            EndStatement(builder);
        }
    }

    /// <inheritdoc />
    protected override void Generate(
        DropIndexOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Table);

        builder.Append("DROP INDEX ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" ON ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table));

        if (terminate)
        {
            builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
            EndStatement(builder);
        }
    }

    /// <inheritdoc />
    protected override void Generate(
        DropForeignKeyOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        builder.Append("ALTER TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table))
            .Append(" DROP CONSTRAINT ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name));

        if (terminate)
        {
            builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
            EndStatement(builder);
        }
    }

    /// <inheritdoc />
    protected override void Generate(
        InsertDataOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        for (var row = 0; row < operation.Values.GetLength(0); row++)
        {
            builder.Append("INSERT INTO ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table))
                .Append(" (")
                .Append(string.Join(", ", operation.Columns.Select(Dependencies.SqlGenerationHelper.DelimitIdentifier)))
                .Append(") VALUES (");

            for (var column = 0; column < operation.Columns.Length; column++)
            {
                if (column > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(GenerateSqlLiteral(operation.Values.GetValue(row, column)));
            }

            builder.Append(")");

            if (terminate)
            {
                builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
                EndStatement(builder);
            }
        }
    }

    /// <inheritdoc />
    protected override void Generate(
        DeleteDataOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        for (var row = 0; row < operation.KeyValues.GetLength(0); row++)
        {
            builder.Append("DELETE FROM ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table))
                .Append(" WHERE ");

            for (var column = 0; column < operation.KeyColumns.Length; column++)
            {
                if (column > 0)
                {
                    builder.Append(" AND ");
                }

                builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.KeyColumns[column]))
                    .Append(" = ")
                    .Append(GenerateSqlLiteral(operation.KeyValues.GetValue(row, column)));
            }

            builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
            EndStatement(builder);
        }
    }

    /// <inheritdoc />
    protected override void ColumnDefinition(
        string? schema,
        string table,
        string name,
        ColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(name))
            .Append(" ")
            .Append(GetColumnType(schema, table, name, operation, model));

        if (!operation.IsNullable)
        {
            builder.Append(" NOT NULL");
        }

        if (operation.DefaultValue is not null)
        {
            builder.Append(" DEFAULT ")
                .Append(GenerateSqlLiteral(operation.DefaultValue));
        }
    }

    /// <inheritdoc />
    protected override void PrimaryKeyConstraint(
        AddPrimaryKeyOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        builder.Append("PRIMARY KEY (")
            .Append(string.Join(", ", operation.Columns.Select(Dependencies.SqlGenerationHelper.DelimitIdentifier)))
            .Append(")");
    }

    /// <inheritdoc />
    protected override void ForeignKeyConstraint(
        AddForeignKeyOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        builder.Append("FOREIGN KEY (")
            .Append(string.Join(", ", operation.Columns.Select(Dependencies.SqlGenerationHelper.DelimitIdentifier)))
            .Append(") REFERENCES ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.PrincipalTable))
            .Append(" (")
            .Append(string.Join(", ", (operation.PrincipalColumns ?? operation.Columns).Select(Dependencies.SqlGenerationHelper.DelimitIdentifier)))
            .Append(")");
    }

    private static string GenerateSqlLiteral(object? value)
        => value switch
        {
            string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
            bool boolean => boolean ? "TRUE" : "FALSE",
            DateTime dateTime => "'" + dateTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "'",
            DateTimeOffset dateTimeOffset => "'" + dateTimeOffset.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "'",
            byte[] bytes => "'" + Convert.ToBase64String(bytes) + "'",
            null => "NULL",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL"
        };
}
