using System.Buffers.Binary;
using System.Text;
using SonnetDB.Exceptions;
using SonnetDB.Kv;

namespace SonnetDB.Sequences;

/// <summary>数据库内部的独立持久序列；预留的号码不参与关系事务回滚。</summary>
internal sealed class SequenceManager(string root, KvOptions options) : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private KvKeyspace? _store;
    private bool _disposed;
    private Exception? _persistenceFailure;
    internal Action<string>? AfterPersistTestHook { get; set; }

    internal void Create(string name, bool quoted, long start, long increment, bool ifNotExists)
    {
        ValidateName(name);
        if (increment == 0)
            throw new SequenceException(SequenceException.InvalidDefinition, "序列 INCREMENT BY 不能为零。");
        lock (_sync)
        {
            Open();
            if (_states.TryGetValue(name, out var existing))
            {
                if (ifNotExists && (!quoted || existing.Name == name)) return;
                throw new SequenceException(SequenceException.AlreadyExists, $"sequence '{name}' 已存在，或存在仅大小写不同的名称。");
            }
            if (_states.Count >= 16_384)
                throw new SequenceException(SequenceException.InvalidDefinition, "一个数据库最多支持 16384 个独立序列。");
            var state = new State(name, start, increment, false);
            Persist("create", () => _store!.Put(Key(name), Encode(state)));
            _states.Add(name, state);
        }
    }

    internal bool Drop(string name, bool quoted, bool ifExists)
    {
        lock (_sync)
        {
            Open();
            var state = Find(name, quoted);
            if (state is null)
            {
                if (ifExists) return false;
                throw Missing(name);
            }
            Persist("drop", () => _store!.Delete(Key(state.Name)));
            _states.Remove(state.Name);
            return true;
        }
    }

    internal void ValidateReference(string reference)
    {
        var (name, quoted) = ParseReference(reference);
        lock (_sync)
        {
            Open();
            _ = Find(name, quoted) ?? throw Missing(name);
        }
    }

    internal long Next(string reference, CancellationToken cancellationToken)
    {
        var (name, quoted) = ParseReference(reference);
        cancellationToken.ThrowIfCancellationRequested();
        bool taken = false;
        try
        {
            while (!taken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Monitor.TryEnter(_sync, 25, ref taken);
            }
            Open();
            var state = Find(name, quoted) ?? throw Missing(name);
            if (state.Exhausted)
                throw new SequenceException(SequenceException.Exhausted, $"sequence '{state.Name}' 已耗尽 Int64 范围。");
            bool exhausted = state.Increment > 0
                ? state.Next > long.MaxValue - state.Increment
                : state.Next < long.MinValue - state.Increment;
            var next = state with { Next = exhausted ? state.Next : state.Next + state.Increment, Exhausted = exhausted };
            cancellationToken.ThrowIfCancellationRequested();
            // CAS 的最后取消点位于 WAL 追加前；成功返回的号码必须已经同步持久化。
            var versioned = _store!.GetEntry(Key(state.Name))!;
            Persist("next", () =>
            {
                if (!_store.CompareAndSet(Key(state.Name), versioned.Version, Encode(next), null, cancellationToken).Succeeded)
                    throw new InvalidDataException("独立序列状态发生非预期的外部变更。");
            });
            _states[state.Name] = next;
            return state.Next;
        }
        finally
        {
            if (taken) Monitor.Exit(_sync);
        }
    }

    internal T ExecuteConsistentBackup<T>(Func<T> copy)
    {
        lock (_sync)
        {
            if (_persistenceFailure is not null)
                throw new SequenceException(SequenceException.RecoveryRequired, "独立序列持久化结果不确定，无法创建一致备份。");
            _store?.CreateSnapshot();
            return copy();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _store?.Dispose();
        }
    }

    private void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_persistenceFailure is not null)
            throw new SequenceException(SequenceException.RecoveryRequired, "独立序列持久化结果不确定；关闭并重新打开数据库后恢复。");
        if (_store is not null) return;
        var store = KvKeyspace.Open("sql-sequences", root, options with { SyncWalOnEveryWrite = true });
        try
        {
            foreach (var entry in store.ScanPrefix([(byte)'s'], limit: 16_385))
            {
                if (_states.Count >= 16_384) throw new InvalidDataException("独立序列目录超出数量上限。");
                var state = Decode(entry.Value.Span);
                if (!entry.Key.Span.SequenceEqual(Key(state.Name)) || !_states.TryAdd(state.Name, state))
                    throw new InvalidDataException("独立序列目录包含冲突或无效名称。");
            }
            _store = store;
        }
        catch
        {
            _states.Clear();
            store.Dispose();
            throw;
        }
    }

    private State? Find(string name, bool quoted)
        => _states.TryGetValue(name, out var state) && (!quoted || state.Name == name) ? state : null;

    private void Persist(string operation, Action action)
    {
        try
        {
            action();
            AfterPersistTestHook?.Invoke(operation);
        }
        catch (OperationCanceledException)
        {
            // KV CAS 保证取消只发生在首次 WAL 追加前，故未消耗号码。
            throw;
        }
        catch (Exception exception)
        {
            // WAL/fsync 报错时提交结果可能未知；绝不能再次使用旧内存状态发号。
            _persistenceFailure = exception;
            throw;
        }
    }

    private static SequenceException Missing(string name)
        => new(SequenceException.NotFound, $"sequence '{name}' 不存在。");

    private static byte[] Key(string name) => Encoding.UTF8.GetBytes("s" + name);

    private static byte[] Encode(State state)
    {
        byte[] name = Encoding.UTF8.GetBytes(state.Name);
        byte[] bytes = new byte[22 + name.Length];
        bytes[0] = 1; // 独立状态 v1，不修改任何已发布文件头或表 schema 布局。
        bytes[1] = state.Exhausted ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(2), state.Next);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(10), state.Increment);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), name.Length);
        name.CopyTo(bytes, 22);
        return bytes;
    }

    private static State Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 22 || bytes[0] != 1 || bytes[1] > 1
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[18..]) != bytes.Length - 22)
            throw new InvalidDataException("独立序列状态版本或长度无效。");
        string name = new UTF8Encoding(false, true).GetString(bytes[22..]);
        ValidateName(name);
        long increment = BinaryPrimitives.ReadInt64LittleEndian(bytes[10..]);
        if (increment == 0) throw new InvalidDataException("独立序列步长无效。");
        return new State(name, BinaryPrimitives.ReadInt64LittleEndian(bytes[2..]), increment, bytes[1] == 1);
    }

    private static (string Name, bool Quoted) ParseReference(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        // 字符串中的双引号遵循 SQL 标识符转义；单 schema 首版拒绝限定名。
        if (reference[0] == '"')
        {
            var name = new StringBuilder();
            for (int i = 1; i < reference.Length; i++)
            {
                if (reference[i] != '"') { name.Append(reference[i]); continue; }
                if (i + 1 < reference.Length && reference[i + 1] == '"') { name.Append('"'); i++; continue; }
                if (i != reference.Length - 1) break;
                ValidateName(name.ToString());
                return (name.ToString(), true);
            }
            throw new SequenceException(SequenceException.InvalidDefinition, "nextval 的引用必须是单个 SQL 标识符。");
        }
        if (reference.Any(static c => !(char.IsLetterOrDigit(c) || c == '_')))
            throw new SequenceException(SequenceException.InvalidDefinition, "nextval 首版仅支持单 schema；特殊名称须使用双引号标识符。");
        ValidateName(reference);
        return (reference, false);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl))
            throw new SequenceException(SequenceException.InvalidDefinition, "序列名称必须为 1 至 256 个非控制字符。");
    }

    private sealed record State(string Name, long Next, long Increment, bool Exhausted);
}
