# SonnetDB.Caching

`SonnetDB.Caching` provides EasyCaching and `IDistributedCache` providers backed by SonnetDB KV keyspaces.

Local SonnetDB connection strings point to a database directory, not a single database file. Remote connection strings access SonnetDB Server over HTTP through `SonnetDB.Data`.

This extension is not marked as Native AOT compatible because it depends on external caching abstractions and `SonnetDB.Data`, which keeps the ADO.NET boundary non-AOT.

## Features

- EasyCaching provider registration.
- Optional `IDistributedCache` implementation.
- TTL support through SonnetDB KV `expiresAtUtc` metadata.
- Lazy expiration plus optional background expired-key cleanup.
- Namespaces, batch get/set/remove, prefix delete, and prefix scans.

## Usage

```csharp
using SonnetDB.Caching;

services.AddSonnetDbEasyCaching("default", options =>
{
    options.ConnectionString = "Data Source=sonnetdb+http://127.0.0.1:5080/app;Token=your-token;Timeout=30";
    options.Keyspace = "cache";
    options.Namespace = "myapp";
});

services.AddSonnetDbDistributedCache(options =>
{
    options.ConnectionString = "Data Source=sonnetdb+http://127.0.0.1:5080/app;Token=your-token;Timeout=30";
    options.Keyspace = "cache";
    options.Namespace = "myapp";
});
```

The provider accesses KV through `SonnetDB.Data`. Applications should configure a SonnetDB connection string and avoid directly opening SonnetDB KV storage files from caching code.
