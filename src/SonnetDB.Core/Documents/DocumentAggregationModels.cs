namespace SonnetDB.Documents;

/// <summary>
/// 文档聚合管线定义。
/// </summary>
/// <param name="Stages">按顺序执行的聚合阶段。</param>
public sealed record DocumentAggregationPipeline(IReadOnlyList<DocumentAggregationStage> Stages);

/// <summary>
/// 文档聚合阶段基类。
/// </summary>
public abstract record DocumentAggregationStage;

/// <summary>
/// `$match` 阶段，复用文档查询过滤表达式。
/// </summary>
/// <param name="Filter">过滤表达式。</param>
public sealed record DocumentMatchStage(DocumentFilter Filter) : DocumentAggregationStage;

/// <summary>
/// `$project` 阶段，按指定字段输出 JSON 对象。
/// </summary>
/// <param name="Projection">投影定义。</param>
/// <param name="ComputedFields">可选的计算字段；同名时覆盖普通投影字段。</param>
public sealed record DocumentProjectStage(
    DocumentProjection Projection,
    IReadOnlyList<DocumentAggregationComputedField>? ComputedFields = null) : DocumentAggregationStage
{
    /// <summary>创建不含计算字段的投影阶段，保留旧版构造入口。</summary>
    public DocumentProjectStage(DocumentProjection Projection)
        : this(Projection, null)
    {
    }

    /// <summary>按旧版字段形态解构投影阶段。</summary>
    public void Deconstruct(out DocumentProjection Projection)
        => Projection = this.Projection;
}

/// <summary>
/// `$project` 的一个计算字段。
/// </summary>
/// <param name="Name">输出字段名。</param>
/// <param name="Expression">字段值表达式。</param>
public sealed record DocumentAggregationComputedField(
    string Name,
    DocumentAggregationExpression Expression);

/// <summary>
/// `$group` 阶段，按 JSON path 或 `_id` 分组并计算聚合值。
/// </summary>
/// <param name="Keys">分组键列表；为空时表示全局分组。</param>
/// <param name="Accumulators">聚合函数列表。</param>
public sealed record DocumentGroupStage(
    IReadOnlyList<DocumentAggregationGroupKey> Keys,
    IReadOnlyList<DocumentAggregationAccumulator> Accumulators) : DocumentAggregationStage;

/// <summary>
/// 文档聚合分组键。
/// </summary>
/// <param name="Name">输出字段名。</param>
/// <param name="Field">兼容旧调用方的输入字段引用；表达式形态会忽略该值。</param>
/// <param name="Expression">可选分组表达式；优先于 <paramref name="Field"/>。</param>
public sealed record DocumentAggregationGroupKey(
    string Name,
    DocumentFieldRef Field,
    DocumentAggregationExpression? Expression = null)
{
    /// <summary>创建字段分组键，保留旧版构造入口。</summary>
    /// <param name="Name">输出字段名。</param>
    /// <param name="Field">输入字段引用。</param>
    public DocumentAggregationGroupKey(string Name, DocumentFieldRef Field)
        : this(Name, Field, null)
    {
    }

    /// <summary>创建使用表达式的分组键。</summary>
    /// <param name="name">输出字段名。</param>
    /// <param name="expression">分组表达式。</param>
    /// <returns>使用指定表达式的分组键。</returns>
    public static DocumentAggregationGroupKey FromExpression(
        string name,
        DocumentAggregationExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new DocumentAggregationGroupKey(name, DocumentFieldRef.Id, expression);
    }

    /// <summary>按旧版字段形态解构分组键。</summary>
    public void Deconstruct(out string Name, out DocumentFieldRef Field)
    {
        Name = this.Name;
        Field = this.Field;
    }
}

/// <summary>
/// 文档聚合函数定义。
/// </summary>
/// <param name="Name">输出字段名。</param>
/// <param name="Operator">聚合函数。</param>
/// <param name="Field">兼容旧调用方的输入字段引用；`count` 或使用表达式时可为空。</param>
/// <param name="Expression">可选输入表达式；优先于 <paramref name="Field"/>。</param>
public sealed record DocumentAggregationAccumulator(
    string Name,
    DocumentAggregationAccumulatorOperator Operator,
    DocumentFieldRef? Field = null,
    DocumentAggregationExpression? Expression = null)
{
    /// <summary>创建字段聚合函数，保留旧版构造入口。</summary>
    public DocumentAggregationAccumulator(
        string Name,
        DocumentAggregationAccumulatorOperator Operator,
        DocumentFieldRef? Field)
        : this(Name, Operator, Field, null)
    {
    }

    /// <summary>创建使用表达式输入的聚合函数。</summary>
    /// <param name="name">输出字段名。</param>
    /// <param name="operator">聚合函数。</param>
    /// <param name="expression">输入表达式。</param>
    /// <returns>使用指定表达式输入的聚合函数。</returns>
    public static DocumentAggregationAccumulator FromExpression(
        string name,
        DocumentAggregationAccumulatorOperator @operator,
        DocumentAggregationExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new DocumentAggregationAccumulator(name, @operator, Field: null, expression);
    }

    /// <summary>按旧版字段形态解构聚合函数。</summary>
    public void Deconstruct(
        out string Name,
        out DocumentAggregationAccumulatorOperator Operator,
        out DocumentFieldRef? Field)
    {
        Name = this.Name;
        Operator = this.Operator;
        Field = this.Field;
    }
}

/// <summary>
/// 文档聚合函数类型。
/// </summary>
public enum DocumentAggregationAccumulatorOperator
{
    /// <summary>计数。</summary>
    Count,
    /// <summary>求和。</summary>
    Sum,
    /// <summary>平均值。</summary>
    Average,
    /// <summary>最小值。</summary>
    Min,
    /// <summary>最大值。</summary>
    Max,
    /// <summary>第一条输入值。</summary>
    First,
    /// <summary>最后一条输入值。</summary>
    Last,
    /// <summary>组内去重值数组。</summary>
    Distinct,
    /// <summary>按输入顺序保留组内全部值。</summary>
    Push,
    /// <summary>按首次出现顺序保留组内去重值。</summary>
    AddToSet,
}

/// <summary>
/// 文档聚合表达式基类。
/// </summary>
public abstract record DocumentAggregationExpression;

/// <summary>读取一个文档字段。</summary>
/// <param name="Field">要读取的字段引用。</param>
public sealed record DocumentAggregationFieldExpression(DocumentFieldRef Field)
    : DocumentAggregationExpression;

/// <summary>返回一个字面量。</summary>
/// <param name="Value">字面量值。</param>
public sealed record DocumentAggregationLiteralExpression(object? Value)
    : DocumentAggregationExpression;

/// <summary>数值加法。</summary>
/// <param name="Left">左操作数。</param>
/// <param name="Right">右操作数。</param>
public sealed record DocumentAggregationAddExpression(
    DocumentAggregationExpression Left,
    DocumentAggregationExpression Right) : DocumentAggregationExpression;

/// <summary>数值减法。</summary>
/// <param name="Left">左操作数。</param>
/// <param name="Right">右操作数。</param>
public sealed record DocumentAggregationSubtractExpression(
    DocumentAggregationExpression Left,
    DocumentAggregationExpression Right) : DocumentAggregationExpression;

/// <summary>数值乘法。</summary>
/// <param name="Left">左操作数。</param>
/// <param name="Right">右操作数。</param>
public sealed record DocumentAggregationMultiplyExpression(
    DocumentAggregationExpression Left,
    DocumentAggregationExpression Right) : DocumentAggregationExpression;

/// <summary>数值除法；除数为零时稳定拒绝。</summary>
/// <param name="Left">被除数。</param>
/// <param name="Right">除数。</param>
public sealed record DocumentAggregationDivideExpression(
    DocumentAggregationExpression Left,
    DocumentAggregationExpression Right) : DocumentAggregationExpression;

/// <summary>按顺序拼接字符串；任一输入为 null 时返回 null。</summary>
/// <param name="Values">按顺序拼接的表达式。</param>
public sealed record DocumentAggregationConcatExpression(
    IReadOnlyList<DocumentAggregationExpression> Values) : DocumentAggregationExpression;

/// <summary>输入为 null 或字段缺失时使用替代值。</summary>
/// <param name="Input">首选输入表达式。</param>
/// <param name="Replacement">输入为空时使用的表达式。</param>
public sealed record DocumentAggregationIfNullExpression(
    DocumentAggregationExpression Input,
    DocumentAggregationExpression Replacement) : DocumentAggregationExpression;

/// <summary>按条件真假选择两个表达式之一。</summary>
/// <param name="Condition">条件表达式。</param>
/// <param name="IfTrue">条件为真时的表达式。</param>
/// <param name="IfFalse">条件为假时的表达式。</param>
public sealed record DocumentAggregationCondExpression(
    DocumentAggregationExpression Condition,
    DocumentAggregationExpression IfTrue,
    DocumentAggregationExpression IfFalse) : DocumentAggregationExpression;

/// <summary>
/// `$sort` 阶段。
/// </summary>
/// <param name="Sort">排序字段列表。</param>
public sealed record DocumentSortStage(IReadOnlyList<DocumentSort> Sort) : DocumentAggregationStage;

/// <summary>
/// `$limit` 阶段。
/// </summary>
/// <param name="Limit">最多输出的文档数。</param>
public sealed record DocumentLimitStage(int Limit) : DocumentAggregationStage;

/// <summary>
/// `$skip` 阶段。
/// </summary>
/// <param name="Skip">跳过的文档数。</param>
public sealed record DocumentSkipStage(int Skip) : DocumentAggregationStage;

/// <summary>
/// `$unwind` 阶段，将数组字段展开为多条文档。
/// </summary>
/// <param name="Field">要展开的字段。</param>
/// <param name="Name">可选输出别名；为空时替换原字段。</param>
/// <param name="PreserveNullAndEmptyArrays">数组为空或字段缺失时是否保留原文档。</param>
/// <param name="IncludeArrayIndex">可选的数组下标输出字段名；非数组或保留行写入 null。</param>
public sealed record DocumentUnwindStage(
    DocumentFieldRef Field,
    string? Name = null,
    bool PreserveNullAndEmptyArrays = false,
    string? IncludeArrayIndex = null) : DocumentAggregationStage
{
    /// <summary>创建不输出数组下标的 unwind，保留旧版构造入口。</summary>
    public DocumentUnwindStage(
        DocumentFieldRef Field,
        string? Name,
        bool PreserveNullAndEmptyArrays)
        : this(Field, Name, PreserveNullAndEmptyArrays, null)
    {
    }

    /// <summary>按旧版字段形态解构 unwind。</summary>
    public void Deconstruct(
        out DocumentFieldRef Field,
        out string? Name,
        out bool PreserveNullAndEmptyArrays)
    {
        Field = this.Field;
        Name = this.Name;
        PreserveNullAndEmptyArrays = this.PreserveNullAndEmptyArrays;
    }
}

/// <summary>
/// `$count` 阶段。
/// </summary>
/// <param name="Name">输出计数字段名。</param>
public sealed record DocumentCountStage(string Name = "count") : DocumentAggregationStage;

/// <summary>
/// `$distinct` 等价阶段，输出指定字段的去重值。
/// </summary>
/// <param name="Field">去重字段。</param>
/// <param name="Name">输出字段名。</param>
/// <param name="Limit">最多输出的去重值数量。</param>
public sealed record DocumentDistinctStage(
    DocumentFieldRef Field,
    string Name = "value",
    int? Limit = null) : DocumentAggregationStage;

/// <summary>
/// 文档聚合执行结果。
/// </summary>
/// <param name="Documents">聚合输出的紧凑 JSON 文档。</param>
public sealed record DocumentAggregationResult(IReadOnlyList<string> Documents);
