namespace SonnetDB.Caching.EasyCaching;

internal sealed class SonnetDbEasyCachingRegistration : IDisposable
{
    public SonnetDbEasyCachingRegistration(string name, SonnetDbEasyCachingOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);

        Name = name;
        Store = new SonnetDbEasyCachingStore(options);
    }

    public string Name { get; }

    public SonnetDbEasyCachingStore Store { get; }

    public void Dispose() => Store.Dispose();
}
