using System.Data;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;
using SonnetDB.Data;

namespace SonnetDB.EntityFrameworkCore.Migrations.Internal;

/// <summary>
/// SonnetDB EF Core migrations history table support.
/// </summary>
public sealed class SonnetDbHistoryRepository : HistoryRepository
{
    /// <summary>
    /// Creates the SonnetDB migrations history repository.
    /// </summary>
    /// <param name="dependencies">History repository dependencies.</param>
    public SonnetDbHistoryRepository(HistoryRepositoryDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc />
    public override LockReleaseBehavior LockReleaseBehavior => LockReleaseBehavior.Connection;

    /// <inheritdoc />
    protected override string ExistsSql => "SHOW TABLES";

    /// <inheritdoc />
    protected override bool InterpretExistsResult(object? value)
        => value is not null
            && string.Equals(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), TableName, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Exists()
    {
        var connection = Dependencies.Connection.DbConnection;
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed)
        {
            connection.Open();
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = ExistsSql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (InterpretExistsResult(reader.GetValue(0)))
                {
                    return true;
                }
            }

            return false;
        }
        catch (SndbServerException ex) when (IsDatabaseNotFound(ex))
        {
            return false;
        }
        finally
        {
            if (wasClosed)
            {
                connection.Close();
            }
        }
    }

    /// <inheritdoc />
    public override async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
    {
        var connection = Dependencies.Connection.DbConnection;
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ExistsSql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (InterpretExistsResult(reader.GetValue(0)))
                {
                    return true;
                }
            }

            return false;
        }
        catch (SndbServerException ex) when (IsDatabaseNotFound(ex))
        {
            return false;
        }
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public override string GetCreateIfNotExistsScript()
        => BuildCreateScript(ifNotExists: true);

    /// <inheritdoc />
    public override string GetCreateScript()
        => BuildCreateScript(ifNotExists: false);

    /// <inheritdoc />
    public override IReadOnlyList<HistoryRow> GetAppliedMigrations()
    {
        var connection = Dependencies.Connection.DbConnection;
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed)
        {
            connection.Open();
        }

        try
        {
            if (!Exists())
            {
                return [];
            }

            using var command = connection.CreateCommand();
            command.CommandText = BuildAppliedMigrationsSql();
            using var reader = command.ExecuteReader();
            var rows = new List<HistoryRow>();
            while (reader.Read())
            {
                rows.Add(new HistoryRow(reader.GetString(0), reader.GetString(1)));
            }

            return rows;
        }
        catch (SndbServerException ex) when (IsDatabaseNotFound(ex) || IsHistoryTableNotFound(ex))
        {
            return [];
        }
        finally
        {
            if (wasClosed)
            {
                connection.Close();
            }
        }
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<HistoryRow>> GetAppliedMigrationsAsync(CancellationToken cancellationToken = default)
    {
        var connection = Dependencies.Connection.DbConnection;
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (!await ExistsAsync(cancellationToken).ConfigureAwait(false))
            {
                return [];
            }

            await using var command = connection.CreateCommand();
            command.CommandText = BuildAppliedMigrationsSql();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var rows = new List<HistoryRow>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new HistoryRow(reader.GetString(0), reader.GetString(1)));
            }

            return rows;
        }
        catch (SndbServerException ex) when (IsDatabaseNotFound(ex) || IsHistoryTableNotFound(ex))
        {
            return [];
        }
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public override IMigrationsDatabaseLock AcquireDatabaseLock()
        => new NoopMigrationsDatabaseLock(this);

    /// <inheritdoc />
    public override Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IMigrationsDatabaseLock>(new NoopMigrationsDatabaseLock(this));
    }

    /// <inheritdoc />
    public override string GetBeginIfNotExistsScript(string migrationId)
        => string.Empty;

    /// <inheritdoc />
    public override string GetBeginIfExistsScript(string migrationId)
        => string.Empty;

    /// <inheritdoc />
    public override string GetEndIfScript()
        => string.Empty;

    private string BuildCreateScript(bool ifNotExists)
    {
        var helper = Dependencies.SqlGenerationHelper;
        return "CREATE TABLE "
            + (ifNotExists ? "IF NOT EXISTS " : string.Empty)
            + helper.DelimitIdentifier(TableName)
            + " ("
            + helper.DelimitIdentifier(MigrationIdColumnName)
            + " STRING NOT NULL, "
            + helper.DelimitIdentifier(ProductVersionColumnName)
            + " STRING NOT NULL, PRIMARY KEY ("
            + helper.DelimitIdentifier(MigrationIdColumnName)
            + "));"
            + Environment.NewLine;
    }

    private string BuildAppliedMigrationsSql()
    {
        var helper = Dependencies.SqlGenerationHelper;
        return "SELECT "
            + helper.DelimitIdentifier(MigrationIdColumnName)
            + ", "
            + helper.DelimitIdentifier(ProductVersionColumnName)
            + " FROM "
            + helper.DelimitIdentifier(TableName)
            + " ORDER BY "
            + helper.DelimitIdentifier(MigrationIdColumnName)
            + Dependencies.SqlGenerationHelper.StatementTerminator;
    }

    private static bool IsDatabaseNotFound(SndbServerException ex)
        => ex.StatusCode == HttpStatusCode.NotFound
            && string.Equals(ex.Error, "db_not_found", StringComparison.Ordinal);

    private bool IsHistoryTableNotFound(SndbServerException ex)
        => string.Equals(ex.Error, "sql_error", StringComparison.Ordinal)
            && ex.ServerMessage.Contains(TableName, StringComparison.Ordinal)
            && ex.ServerMessage.Contains("不存在", StringComparison.Ordinal);

    private sealed class NoopMigrationsDatabaseLock(IHistoryRepository historyRepository) : IMigrationsDatabaseLock
    {
        public IHistoryRepository HistoryRepository { get; } = historyRepository;

        public IMigrationsDatabaseLock ReacquireIfNeeded(bool connectionReopened, bool? transactionRestarted)
            => this;

        public Task<IMigrationsDatabaseLock> ReacquireIfNeededAsync(
            bool connectionReopened,
            bool? transactionRestarted,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IMigrationsDatabaseLock>(this);
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }
}
