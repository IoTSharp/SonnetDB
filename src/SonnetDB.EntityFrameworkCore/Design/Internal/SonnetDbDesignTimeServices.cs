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

        new EntityFrameworkDesignServicesBuilder(serviceCollection)
            .TryAddProviderSpecificServices(
                services =>
                {
                    services.TryAddSingleton<AnnotationCodeGeneratorDependencies, AnnotationCodeGeneratorDependencies>();
                    services.TryAddSingleton<IAnnotationCodeGenerator, AnnotationCodeGenerator>();
                })
            .TryAddCoreServices();
    }
}
