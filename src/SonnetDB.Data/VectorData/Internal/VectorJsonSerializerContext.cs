using System.Text.Json.Serialization;

namespace SonnetDB.Data.VectorData.Internal;

[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(float[]))]
internal sealed partial class VectorJsonSerializerContext : JsonSerializerContext;
