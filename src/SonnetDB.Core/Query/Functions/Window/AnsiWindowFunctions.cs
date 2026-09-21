using SonnetDB.Catalog;
using SonnetDB.Model;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Query.Functions.Window;

/// <summary>ANSI <c>row_number()</c> 窗口函数。</summary>
internal sealed class RowNumberFunction : IWindowFunction
{
    public string Name => "row_number";

    public IWindowEvaluator CreateEvaluator(FunctionCallExpression call, MeasurementSchema schema)
    {
        WindowFunctionBinder.RequireArgumentCount(call, Name, 0, 0);
        if (call.Over is null)
            throw new InvalidOperationException("窗口函数 row_number 必须带 OVER (...)。");
        return new RowNumberEvaluator();
    }
}

/// <summary>按当前 measurement 的时间升序为每个 series 编号的 row_number 求值器。</summary>
internal sealed class RowNumberEvaluator : IWindowStreamingEvaluator
{
    public string FieldName => string.Empty;

    public object?[] Compute(long[] timestamps, FieldValue?[] values)
    {
        ArgumentNullException.ThrowIfNull(timestamps);
        ArgumentNullException.ThrowIfNull(values);
        if (timestamps.Length != values.Length)
            throw new ArgumentException("窗口函数输入数组长度不一致。", nameof(values));

        var output = new object?[timestamps.Length];
        for (int i = 0; i < output.Length; i++)
            output[i] = (long)i + 1;
        return output;
    }

    public IWindowState CreateState() => new RowNumberState();
}

internal sealed class RowNumberState : IWindowState
{
    private long _rowNumber;

    public WindowStateOutput Update(long timestamp, FieldValue? value)
        => WindowStateOutput.FromObject(++_rowNumber);
}
