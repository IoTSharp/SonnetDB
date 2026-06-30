using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.EntityFrameworkCore.Diagnostics.Internal;
using SonnetDB.EntityFrameworkCore.Metadata.Conventions.Internal;
using SonnetDB.EntityFrameworkCore.Migrations.Internal;
using SonnetDB.EntityFrameworkCore.Query.Internal;
using SonnetDB.EntityFrameworkCore.Storage.Internal;
using SonnetDB.EntityFrameworkCore.Update.Internal;
using SonnetDB.EntityFrameworkCore.ValueGeneration.Internal;

namespace SonnetDB.EntityFrameworkCore.Extensions;

/// <summary>
/// 提供 SonnetDB Entity Framework Core Provider 的服务注册扩展。
/// </summary>
public static class SonnetDbServiceCollectionExtensions
{
    /// <summary>
    /// 向服务集合注册 SonnetDB EF Core Provider 的运行时服务。
    /// </summary>
    /// <param name="serviceCollection">服务集合。</param>
    /// <returns>传入的服务集合。</returns>
    public static IServiceCollection AddEntityFrameworkSonnetDB(this IServiceCollection serviceCollection)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);

        new EntityFrameworkRelationalServicesBuilder(serviceCollection)
            .TryAdd<LoggingDefinitions, SonnetDbLoggingDefinitions>()
            .TryAdd<IDatabaseProvider, SonnetDbDatabaseProvider>()
            .TryAdd<IProviderConventionSetBuilder, SonnetDbConventionSetBuilder>()
            .TryAdd<IRelationalConnection, SonnetDbRelationalConnection>()
            .TryAdd<IRelationalDatabaseCreator, SonnetDbDatabaseCreator>()
            .TryAdd<IQueryCompilationContextFactory, SonnetDbQueryCompilationContextFactory>()
            .TryAdd<IRelationalTypeMappingSource, SonnetDbTypeMappingSource>()
            .TryAdd<ISqlGenerationHelper, SonnetDbSqlGenerationHelper>()
            .TryAdd<IQuerySqlGeneratorFactory, SonnetDbQuerySqlGeneratorFactory>()
            .TryAdd<IMethodCallTranslatorProvider, SonnetDbMethodCallTranslatorProvider>()
            .TryAdd<IUpdateSqlGenerator, SonnetDbUpdateSqlGenerator>()
            .TryAdd<IModificationCommandBatchFactory, SonnetDbModificationCommandBatchFactory>()
            .TryAdd<IMigrationsSqlGenerator, SonnetDbMigrationsSqlGenerator>()
            .TryAdd<IHistoryRepository, SonnetDbHistoryRepository>()
            .TryAdd<IValueGeneratorSelector, SonnetDbValueGeneratorSelector>()
            .TryAddCoreServices();

        return serviceCollection;
    }
}
