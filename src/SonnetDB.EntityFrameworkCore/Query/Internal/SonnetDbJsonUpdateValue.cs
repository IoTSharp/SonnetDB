using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.Json;
using SonnetDB.EntityFrameworkCore.Storage.Internal;

namespace SonnetDB.EntityFrameworkCore.Query.Internal;

// EF's model supplies names, accessors and scalar JSON codecs. No reflection serializer is used.
internal static class SonnetDbJsonUpdateValue
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private const int MaxNodes = 100_000;

    internal static string? Serialize(object? value, IProperty? property, ITypeBase? type, JsonValueReaderWriter? codec, bool collection,
        CancellationToken cancellationToken)
    {
        if (value is null)
            return null;
        var budget = new Budget();
        using var stream = new BoundedStream(budget, cancellationToken);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { MaxDepth = 64 });
        if (property is not null)
            WriteScalar(value, property, 0);
        else if (codec is not null && type is null)
        {
            Check(0);
            if (value is byte[] binary && binary.Length > MaxBytes)
                throw new InvalidOperationException("JSON update value exceeds its byte budget.");
            codec.ToJson(writer, value);
            Check(0);
        }
        else
        {
            ArgumentNullException.ThrowIfNull(type);
            Write(value, type, collection, 0);
        }
        writer.Flush();
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));

        void Check(int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++budget.Nodes > MaxNodes || depth > 64 || budget.Timer.Elapsed > TimeSpan.FromSeconds(10)
                || writer.BytesCommitted + writer.BytesPending > MaxBytes)
                throw new InvalidOperationException("JSON update value exceeds its node, depth, time or byte budget.");
        }

        void Write(object? item, ITypeBase itemType, bool many, int depth)
        {
            Check(depth);
            if (item is null)
            {
                writer.WriteNullValue();
                return;
            }
            if (many)
            {
                if (item is not IEnumerable values)
                    throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
                writer.WriteStartArray();
                foreach (var element in values)
                    Write(element, itemType, false, depth + 1);
                writer.WriteEndArray();
                Check(depth);
                return;
            }

            writer.WriteStartObject();
            foreach (var scalar in itemType.GetProperties())
            {
                if (scalar.IsShadowProperty() || scalar.GetJsonPropertyName() is not { } name)
                    continue;
                Check(depth + 1);
                writer.WritePropertyName(name);
                var scalarValue = scalar.GetGetter().GetClrValue(item);
                if (scalarValue is null)
                    writer.WriteNullValue();
                else
                    WriteScalar(scalarValue, scalar, depth + 1);
                Check(depth + 1);
            }
            foreach (var complex in itemType.GetComplexProperties())
            {
                writer.WritePropertyName(complex.GetJsonPropertyName() ?? complex.Name);
                Write(complex.GetGetter().GetClrValue(item), complex.ComplexType, complex.IsCollection, depth + 1);
            }
            if (itemType is IEntityType entity)
            {
                foreach (var navigation in entity.GetNavigations())
                {
                    if (!navigation.ForeignKey.IsOwnership || navigation.IsOnDependent)
                        continue;
                    writer.WritePropertyName(navigation.TargetEntityType.GetJsonPropertyName() ?? navigation.Name);
                    Write(navigation.GetGetter().GetClrValue(item), navigation.TargetEntityType, navigation.IsCollection, depth + 1);
                }
            }
            writer.WriteEndObject();
            Check(depth);
        }

        void WriteScalar(object scalarValue, IProperty scalar, int depth)
        {
            Check(depth);
            if (scalarValue is string text && text.Length > MaxBytes
                || scalarValue is byte[] bytes && bytes.Length > MaxBytes
                || scalarValue is ICollection items && scalar.IsPrimitiveCollection && items.Count > MaxNodes)
                throw new InvalidOperationException("JSON update value exceeds its collection or byte budget.");
            var scalarCodec = scalar.GetJsonValueReaderWriter() ?? scalar.GetTypeMapping().JsonValueReaderWriter
                ?? throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
            scalarCodec.ToJson(writer, scalarValue);
            Check(depth);
        }
    }

    private sealed class Budget
    {
        public int Nodes;
        public Stopwatch Timer { get; } = Stopwatch.StartNew();
    }

    private sealed class BoundedStream(Budget budget, CancellationToken token) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Validate(buffer.Length);
            base.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Validate(count);
            base.Write(buffer, offset, count);
        }

        private void Validate(int count)
        {
            token.ThrowIfCancellationRequested();
            if (Position + count > MaxBytes || budget.Timer.Elapsed > TimeSpan.FromSeconds(10))
                throw new InvalidOperationException("JSON update value exceeds its byte or time budget.");
        }
    }
}
