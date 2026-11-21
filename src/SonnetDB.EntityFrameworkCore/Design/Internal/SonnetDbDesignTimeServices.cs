using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.EntityFrameworkCore.Extensions;

namespace SonnetDB.EntityFrameworkCore.Design.Internal;

/// <summary>
/// Registers SonnetDB EF Core services used by design-time tooling.
/// </summary>
public sealed class SonnetDbDesignTimeServices : IDesignTimeServices
{
    /// <inheritdoc />
    public void ConfigureDesignTimeServices(IServiceCollection serviceCollection)
    {
        serviceCollection.AddEntityFrameworkSonnetDB();

        // Compiled relational models need the relational runtime annotation
        // generator and its dependencies, not only the base EF model generator.
        new EntityFrameworkRelationalDesignServicesBuilder(serviceCollection)
            .TryAddCoreServices();
    }
}
