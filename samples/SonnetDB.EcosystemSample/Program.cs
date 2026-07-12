using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Data;
using SonnetDB.Data.ObjectStorage;
using SonnetDB.EntityFrameworkCore.Extensions;
using SonnetDB.ObjectStorage;

string connectionString = Environment.GetEnvironmentVariable("SONNETDB_CONNECTION")
    ?? "Data Source=./sample-data";

await SndbResourceInitializer.EnsureDatabaseAsync(connectionString, "生态接入样例");

var services = new ServiceCollection();
services.AddDbContext<SampleDbContext>(options => options.UseSonnetDB(connectionString));
services.AddDistributedSonnetDBCache(options =>
{
    options.ConnectionString = connectionString;
    options.Keyspace = "sample-cache";
    options.Namespace = "ecosystem";
});

await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();

var database = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
await database.Database.EnsureCreatedAsync();
if (!await database.Products.AnyAsync())
{
    database.Products.Add(new Product { Id = 1, Name = "Edge Gateway" });
    await database.SaveChangesAsync();
}

var cache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();
await cache.SetStringAsync(
    "product:1",
    "ready",
    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });

using var objects = new SndbObjectStorageClient(connectionString);
var buckets = await objects.ListBucketsAsync();
if (!buckets.Any(bucket => bucket.Name == "sample-artifacts"))
{
    await objects.CreateBucketAsync("sample-artifacts", SndbBucketPurpose.Artifact);
}

using var objectContent = new MemoryStream(Encoding.UTF8.GetBytes("SonnetDB ecosystem sample"));
await objects.PutObjectAsync(
    "sample-artifacts",
    "hello.txt",
    objectContent,
    "text/plain");

using var connection = new SndbConnection(connectionString);
await connection.OpenAsync();
using var command = connection.CreateCommand();
command.CommandText = "SELECT count(*) FROM products";

Console.WriteLine($"mode={connection.ProviderMode}");
Console.WriteLine($"products={await command.ExecuteScalarAsync()}");
Console.WriteLine($"cache={await cache.GetStringAsync("product:1")}");
Console.WriteLine($"objects={(await objects.ListObjectsAsync("sample-artifacts")).Objects.Count}");

internal sealed class SampleDbContext(DbContextOptions<SampleDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("products");
            entity.HasKey(product => product.Id);
            entity.Property(product => product.Name).HasMaxLength(200).IsRequired();
        });
    }
}

internal sealed class Product
{
    public int Id { get; set; }

    public required string Name { get; set; }
}
