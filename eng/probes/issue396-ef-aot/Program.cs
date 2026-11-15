using Microsoft.EntityFrameworkCore;

namespace SonnetDbEfAotProbe;

internal sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options)
{
    public DbSet<ProbeDevice> Devices => Set<ProbeDevice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProbeDevice>().HasKey(device => device.Id);
        modelBuilder.Entity<ProbeDevice>().Property(device => device.Name).HasMaxLength(64);
    }
}

internal sealed class ProbeDevice
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; }
}

internal static class Program
{
    private static void Main()
    {
        DbContextOptions<ProbeContext> options = new DbContextOptionsBuilder<ProbeContext>()
            .UseSonnetDB("Data Source=issue396-probe")
            .Options;

        using ProbeContext context = new(options);
        Console.WriteLine(context.Database.ProviderName ?? "<provider-missing>");
        Console.WriteLine(context.Model.FindEntityType(typeof(ProbeDevice))?.Name ?? "<model-missing>");
    }
}
