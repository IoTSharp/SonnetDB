using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace SonnetDB.EntityFrameworkCore.Storage.Internal;

internal sealed class SonnetDbRelationalTransactionFactory(
    RelationalTransactionFactoryDependencies dependencies) : IRelationalTransactionFactory
{
    private readonly RelationalTransactionFactoryDependencies _dependencies = dependencies;

    public RelationalTransaction Create(
        IRelationalConnection connection,
        DbTransaction transaction,
        Guid transactionId,
        IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger,
        bool transactionOwned)
    {
        return new SonnetDbRelationalTransaction(
            connection,
            transaction,
            transactionId,
            logger,
            transactionOwned,
            _dependencies.SqlGenerationHelper);
    }
}
