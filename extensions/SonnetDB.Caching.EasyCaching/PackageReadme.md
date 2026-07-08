# SonnetDB.Caching.EasyCaching

`SonnetDB.Caching.EasyCaching` provides an EasyCaching provider backed by SonnetDB KV keyspaces.

Local SonnetDB connection strings point to a database directory, not a single database file. Remote connection strings access SonnetDB Server over HTTP through `SonnetDB.Data`.

This package is not marked as Native AOT compatible because it depends on EasyCaching abstractions and `SonnetDB.Data`, which keeps the ADO.NET boundary non-AOT.

## Usage

```csharp
using Microsoft.Extensions.DependencyInjection;

services.AddEasyCaching(options =>
{
    options.UseSonnetDB(config =>
    {
        config.ConnectionString = "Data Source=sonnetdb+http://127.0.0.1:5080/app;Token=your-token;Timeout=30";
        config.Keyspace = "cache";
        config.Namespace = "myapp";
    }, "default");
});
```

The provider accesses KV through `SonnetDB.Data`. Applications should configure a SonnetDB connection string and avoid directly opening SonnetDB KV storage files from caching code.
