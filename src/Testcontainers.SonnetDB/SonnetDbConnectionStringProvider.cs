using DotNet.Testcontainers.Configurations;

namespace Testcontainers.SonnetDB;

internal sealed class SonnetDbConnectionStringProvider
    : ContainerConnectionStringProvider<SonnetDbContainer, SonnetDbConfiguration>
{
    protected override string GetHostConnectionString()
        => Container.GetConnectionString();

    public override string GetConnectionString(string name, ConnectionMode connectionMode = ConnectionMode.Host)
        => string.IsNullOrEmpty(name)
            ? base.GetConnectionString(connectionMode)
            : Container.GetConnectionString(name);
}
