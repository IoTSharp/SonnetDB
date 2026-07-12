using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace SonnetDB.EntityFrameworkCore.Storage.Internal;

internal sealed class SonnetDbRelationalTransaction(
    IRelationalConnection connection,
    DbTransaction transaction,
    Guid transactionId,
    IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger,
    bool transactionOwned,
    ISqlGenerationHelper sqlGenerationHelper)
    : RelationalTransaction(
        connection,
        transaction,
        transactionId,
        logger,
        transactionOwned,
        sqlGenerationHelper)
{
    public override bool SupportsSavepoints => false;
}
