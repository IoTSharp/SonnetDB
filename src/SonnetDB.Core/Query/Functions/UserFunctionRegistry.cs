using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Query.Functions;

/// <summary>
/// 用户自定义函数（UDF）注册表，挂在 <see cref="Tsdb.Functions"/> 上，按 <see cref="Tsdb"/> 实例隔离。
/// </summary>
/// <remarks>
/// <para>
/// 与静态 <see cref="FunctionRegistry"/>（内置函数）协作：查询执行时优先匹配用户函数，
/// 名字冲突的用户函数会覆盖同名内置函数；查询结束后 ambient 自动清理。
/// </para>
/// <para>
/// 当 <see cref="TsdbOptions.AllowUserFunctions"/> 为 <c>false</c>（如 Server 模式默认）时，
/// 任何 <c>Register*</c> 方法都会抛出 <see cref="InvalidOperationException"/>。
/// </para>
/// </remarks>
public sealed class UserFunctionRegistry
{
    private static readonly AsyncLocal<UserFunctionRegistry?> _current = new();

    private readonly ConcurrentDictionary<string, IScalarFunction> _scalars
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, IAggregateFunction> _aggregates
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, IWindowFunction> _windows
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, TableValuedFunctionDelegate> _tableValued
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly bool _enabled;

    /// <summary>构造一个 UDF 注册表实例。</summary>
    /// <param name="enabled">是否允许注册；<c>false</c> 时所有 Register 方法抛出。</param>
    public UserFunctionRegistry(bool enabled = true)
    {
        _enabled = enabled;
    }

    /// <summary>当前查询执行作用域内的 UDF 注册表（基于 <see cref="AsyncLocal{T}"/>）。</summary>
    public static UserFunctionRegistry? Current => _current.Value;

    /// <summary>是否允许注册（与 <see cref="TsdbOptions.AllowUserFunctions"/> 对应）。</summary>
    public bool IsEnabled => _enabled;

    /// <summary>已注册的用户标量函数集合。</summary>
    public IReadOnlyCollection<IScalarFunction> Scalars => (IReadOnlyCollection<IScalarFunction>)_scalars.Values;

    /// <summary>已注册的用户聚合函数集合。</summary>
    public IReadOnlyCollection<IAggregateFunction> Aggregates => (IReadOnlyCollection<IAggregateFunction>)_aggregates.Values;

    /// <summary>已注册的用户窗口函数集合。</summary>
    public IReadOnlyCollection<IWindowFunction> Windows => (IReadOnlyCollection<IWindowFunction>)_windows.Values;

    /// <summary>已注册的用户表值函数名集合。</summary>
    public IReadOnlyCollection<string> TableValuedFunctionNames => (IReadOnlyCollection<string>)_tableValued.Keys;

    /// <summary>注册一个标量 UDF（按委托形式快速注册）。</summary>
    /// <param name="name">函数名（大小写不敏感）。</param>
    /// <param name="evaluator">求值委托：接收 <c>object?[]</c> 形态参数，返回任意可序列化值。</param>
    /// <param name="minArgumentCount">最少参数个数，默认 0。</param>
    /// <param name="maxArgumentCount">最多参数个数，默认 <see cref="int.MaxValue"/>。</param>
    public void RegisterScalar(
        string name,
        Func<IReadOnlyList<object?>, object?> evaluator,
        int minArgumentCount = 0,
        int maxArgumentCount = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(evaluator);
        if (minArgumentCount < 0) throw new ArgumentOutOfRangeException(nameof(minArgumentCount));
        if (maxArgumentCount < minArgumentCount) throw new ArgumentOutOfRangeException(nameof(maxArgumentCount));

        RegisterScalar(new DelegateScalarFunction(name, minArgumentCount, maxArgumentCount, evaluator));
    }

    /// <summary>注册一个标量 UDF（提供 <see cref="IScalarFunction"/> 实例，支持自定义校验等）。</summary>
    public void RegisterScalar(IScalarFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        EnsureEnabled();
        _scalars[function.Name] = function;
    }

    /// <summary>注册一个聚合 UDF。</summary>
    public void RegisterAggregate(IAggregateFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        EnsureEnabled();
        if (function.LegacyAggregator is not null)
            throw new InvalidOperationException(
                $"用户聚合函数 '{function.Name}' 不允许设置 LegacyAggregator（仅内置 7 个聚合可用）。");
        _aggregates[function.Name] = function;
    }

    /// <summary>注册一个窗口 UDF。</summary>
    public void RegisterWindow(IWindowFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        EnsureEnabled();
        _windows[function.Name] = function;
    }

    /// <summary>
    /// 注册一个表值 UDF；调用形态：<c>SELECT * FROM &lt;name&gt;(measurement, ...) WHERE ...</c>。
    /// 第 1 个参数当前必须是 measurement 标识符（与内置 <c>forecast</c> 保持一致，由 Parser 强制要求）。
    /// </summary>
    public void RegisterTableValuedFunction(string name, TableValuedFunctionDelegate executor)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(executor);
        EnsureEnabled();
        if (string.Equals(name, "forecast", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("表值函数名 'forecast' 已被内置函数占用。");
        _tableValued[name] = executor;
    }

    /// <summary>移除一个已注册的 UDF（任意类别）。</summary>
    /// <returns>是否实际移除了某个条目。</returns>
    public bool Unregister(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        bool removed = false;
        removed |= _scalars.TryRemove(name, out _);
        removed |= _aggregates.TryRemove(name, out _);
        removed |= _windows.TryRemove(name, out _);
        removed |= _tableValued.TryRemove(name, out _);
        return removed;
    }

    /// <summary>按名称查找标量 UDF。</summary>
    public bool TryGetScalar(string name, [MaybeNullWhen(false)] out IScalarFunction function)
    {
        if (!_scalars.TryGetValue(name, out function)) return false;
        ThrowIfDeferredExecution(name);
        return true;
    }

    /// <summary>按名称查找聚合 UDF。</summary>
    public bool TryGetAggregate(string name, [MaybeNullWhen(false)] out IAggregateFunction function)
    {
        if (!_aggregates.TryGetValue(name, out function)) return false;
        ThrowIfDeferredExecution(name);
        return true;
    }

    /// <summary>按名称查找窗口 UDF。</summary>
    public bool TryGetWindow(string name, [MaybeNullWhen(false)] out IWindowFunction function)
    {
        if (!_windows.TryGetValue(name, out function)) return false;
        ThrowIfDeferredExecution(name);
        return true;
    }

    /// <summary>按名称查找表值 UDF。</summary>
    public bool TryGetTableValuedFunction(string name, [MaybeNullWhen(false)] out TableValuedFunctionDelegate executor)
    {
        if (!_tableValued.TryGetValue(name, out executor)) return false;
        ThrowIfDeferredExecution(name);
        return true;
    }

    private static void ThrowIfDeferredExecution(string name)
    {
        if (SqlTransactionContext.Current?.IsExecutingDeferredTriggers == true)
            throw new RoutineExecutionException(RoutineErrorCodes.Dependency,
                $"提交阶段不允许调用用户函数 '{name}'。");
    }

    /// <summary>把 <paramref name="registry"/> 设为当前查询作用域的 ambient UDF 注册表，返回作用域释放器。</summary>
    public static AmbientScope EnterScope(UserFunctionRegistry? registry)
        => new(registry);

    private void EnsureEnabled()
    {
        if (!_enabled)
            throw new InvalidOperationException(
                "当前 Tsdb 实例已禁用 UDF（TsdbOptions.AllowUserFunctions = false）。");
    }

    /// <summary>表值 UDF 执行委托。</summary>
    /// <param name="tsdb">当前 <see cref="Tsdb"/> 实例。</param>
    /// <param name="statement">完整 SELECT 语句 AST；TVF 调用位于 <see cref="SelectStatement.TableValuedFunction"/>。</param>
    /// <returns>列名 + 行数据组成的执行结果。</returns>
    public delegate SelectExecutionResult TableValuedFunctionDelegate(Tsdb tsdb, SelectStatement statement);

    /// <summary>用于在 <c>using</c> 块中临时设置 ambient UDF 注册表。</summary>
    public readonly struct AmbientScope : IDisposable
    {
        private readonly UserFunctionRegistry? _previous;
        private readonly bool _entered;

        internal AmbientScope(UserFunctionRegistry? registry)
        {
            _previous = _current.Value;
            _current.Value = registry;
            _entered = true;
        }

        /// <summary>恢复进入前的 ambient UDF 注册表。</summary>
        public void Dispose()
        {
            if (_entered)
                _current.Value = _previous;
        }
    }

    private sealed class DelegateScalarFunction : IScalarFunction
    {
        private readonly Func<IReadOnlyList<object?>, object?> _evaluator;

        public DelegateScalarFunction(string name, int min, int max,
            Func<IReadOnlyList<object?>, object?> evaluator)
        {
            Name = name;
            MinArgumentCount = min;
            MaxArgumentCount = max;
            _evaluator = evaluator;
        }

        public string Name { get; }
        public int MinArgumentCount { get; }
        public int MaxArgumentCount { get; }

        public object? Evaluate(IReadOnlyList<object?> args)
        {
            // 即使调用方曾在提交前取得委托适配器，也不能在提交临界区执行回调。
            ThrowIfDeferredExecution(Name);
            ArgumentNullException.ThrowIfNull(args);
            if (args.Count < MinArgumentCount || args.Count > MaxArgumentCount)
            {
                string expected = MinArgumentCount == MaxArgumentCount
                    ? MinArgumentCount.ToString()
                    : $"{MinArgumentCount}~{MaxArgumentCount}";
                throw new InvalidOperationException(
                    $"用户函数 {Name} 需要 {expected} 个参数，实际为 {args.Count}。");
            }

            return _evaluator(args);
        }
    }
}
