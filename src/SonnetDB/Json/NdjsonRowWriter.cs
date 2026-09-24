using System.Buffers;
using System.Text;
using System.Text.Json;
using SonnetDB.Model;

namespace SonnetDB.Json;

/// <summary>
/// 把 <see cref="object"/> 行直接写成 JSON 数组的 AOT 友好工具。
/// <para>
/// 为什么不用 <c>JsonSerializer.Serialize&lt;object&gt;</c>：在 Native AOT 下序列化
/// <c>object</c> 多态需要反射 / 动态代码（IL3050），会被 trim 分析器拒绝。
/// 这里通过 <c>switch</c> + <see cref="Utf8JsonWriter"/> 显式覆盖时序场景下的所有可能值类型，
/// 完全静态可证明。
/// </para>
/// </summary>
public static class NdjsonRowWriter
{
    /// <summary>
    /// 把一行（<see cref="object"/> 数组）写成 JSON 数组形式 <c>[v0, v1, v2]</c>。
    /// </summary>
    public static void WriteRow(Utf8JsonWriter writer, IReadOnlyList<object?> row)
    {
        writer.WriteStartArray();
        for (int i = 0; i < row.Count; i++)
        {
            WriteValue(writer, row[i]);
        }
        writer.WriteEndArray();
    }

    /// <summary>
    /// 写出单个标量值。未识别类型回退到 <c>ToString()</c>（以字符串形式输出，避免抛出）。
    /// </summary>
    public static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case byte u8:
                writer.WriteNumberValue(u8);
                break;
            case sbyte i8:
                writer.WriteNumberValue(i8);
                break;
            case short i16:
                writer.WriteNumberValue(i16);
                break;
            case ushort u16:
                writer.WriteNumberValue(u16);
                break;
            case int i32:
                writer.WriteNumberValue(i32);
                break;
            case uint u32:
                writer.WriteNumberValue(u32);
                break;
            case long i64:
                writer.WriteNumberValue(i64);
                break;
            case ulong u64:
                writer.WriteNumberValue(u64);
                break;
            case float f32:
                if (float.IsFinite(f32)) writer.WriteNumberValue(f32);
                else writer.WriteNullValue();
                break;
            case double f64:
                if (double.IsFinite(f64)) writer.WriteNumberValue(f64);
                else writer.WriteNullValue();
                break;
            case decimal dec:
                writer.WriteNumberValue(dec);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case DateTime dt:
                writer.WriteStringValue(dt);
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto);
                break;
            case TimeOnly time:
                writer.WriteStringValue(time.ToString("HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture));
                break;
            case Guid g:
                writer.WriteStringValue(g);
                break;
            case GeoPoint point:
                GeoJsonWriter.WritePointGeometry(writer, point);
                break;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;
            case float[] vector:
                WriteVector(writer, vector);
                break;
            case Memory<float> vector:
                WriteVector(writer, vector.Span);
                break;
            case ReadOnlyMemory<float> vector:
                WriteVector(writer, vector.Span);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static void WriteVector(Utf8JsonWriter writer, ReadOnlySpan<float> vector)
    {
        if (vector.Length == 0)
            throw new InvalidDataException("VECTOR 结果不能为空。");

        writer.WriteStartArray();
        for (int i = 0; i < vector.Length; i++)
        {
            float value = vector[i];
            if (float.IsFinite(value))
                writer.WriteNumberValue(value);
            else
                throw new InvalidDataException("VECTOR 结果只能包含有限浮点数。");
        }
        writer.WriteEndArray();
    }
}
