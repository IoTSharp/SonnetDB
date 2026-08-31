using System.Text;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class KvConditionalOperationsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-kv-conditional-operations-tests",
        Guid.NewGuid().ToString("N"));

    public KvConditionalOperationsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Set_WithNxAndXx_AppliesOnlyWhenExistenceConditionMatches()
    {
        using var keyspace = Open("conditions");
        long initialWalLength = keyspace.ActiveWalLength;

        KvSetResult missingXx = keyspace.Set("item", [1], KvSetCondition.IfExists);

        Assert.False(missingXx.Applied);
        Assert.Null(missingXx.Version);
        Assert.Equal(initialWalLength, keyspace.ActiveWalLength);

        KvSetResult created = keyspace.Set("item", [2], KvSetCondition.IfNotExists);
        long walLengthAfterCreate = keyspace.ActiveWalLength;
        KvSetResult existingNx = keyspace.Set("item", [3], KvSetCondition.IfNotExists);

        Assert.True(created.Applied);
        Assert.Equal(created.Version, keyspace.GetEntry("item")!.Version);
        Assert.False(existingNx.Applied);
        Assert.Null(existingNx.Version);
        Assert.Equal(walLengthAfterCreate, keyspace.ActiveWalLength);
        Assert.Equal([2], keyspace.Get("item"));

        KvSetResult replaced = keyspace.Set("item", [4], KvSetCondition.IfExists);

        Assert.True(replaced.Applied);
        Assert.True(replaced.Version > created.Version);
        Assert.Equal([4], keyspace.Get("item"));
    }

    [Fact]
    public void Set_WithExpiredKey_TreatsKeyAsMissingForNxAndXx()
    {
        using var keyspace = Open("expired-condition");
        keyspace.Put("item", [1], DateTimeOffset.UtcNow.AddMinutes(-1));
        long walLength = keyspace.ActiveWalLength;

        KvSetResult xx = keyspace.Set("item", [2], KvSetCondition.IfExists);

        Assert.False(xx.Applied);
        Assert.Equal(walLength, keyspace.ActiveWalLength);

        DateTimeOffset replacementExpiry = DateTimeOffset.UtcNow.AddHours(1);
        KvSetResult nx = keyspace.Set(
            "item",
            [3],
            KvSetCondition.IfNotExists,
            replacementExpiry);

        Assert.True(nx.Applied);
        KvEntry entry = keyspace.GetEntry("item")!;
        Assert.Equal([3], entry.Value.ToArray());
        Assert.Equal(replacementExpiry, entry.ExpiresAtUtc);
    }

    [Fact]
    public void GetAndSet_WithExistingValue_ReturnsSnapshotAndReplacesTtl()
    {
        using var keyspace = Open("get-and-set");
        DateTimeOffset oldExpiry = DateTimeOffset.UtcNow.AddMinutes(30);
        long oldVersion = keyspace.Put("item", [1, 2], oldExpiry);

        KvExchangeResult exchanged = keyspace.GetAndSet("item", [3, 4]);

        Assert.NotNull(exchanged.PreviousEntry);
        Assert.Equal("item", Encoding.UTF8.GetString(exchanged.PreviousEntry.Key.Span));
        Assert.Equal([1, 2], exchanged.PreviousEntry.Value.ToArray());
        Assert.Equal(oldVersion, exchanged.PreviousEntry.Version);
        Assert.Equal(oldExpiry, exchanged.PreviousEntry.ExpiresAtUtc);
        Assert.NotNull(exchanged.MutationVersion);
        Assert.Equal([3, 4], keyspace.Get("item"));
        Assert.Equal(KvTtlResult.NoExpiration, keyspace.GetTimeToLive("item").Milliseconds);
    }

    [Fact]
    public void GetAndSet_WithMissingOrExpiredValue_ReturnsNoPreviousEntry()
    {
        using var keyspace = Open("get-and-set-missing");

        KvExchangeResult missing = keyspace.GetAndSet("missing", [1]);
        keyspace.Put("expired", [2], DateTimeOffset.UtcNow.AddMinutes(-1));
        DateTimeOffset expiry = DateTimeOffset.UtcNow.AddHours(1);
        KvExchangeResult expired = keyspace.GetAndSet("expired", [3], expiry);

        Assert.Null(missing.PreviousEntry);
        Assert.NotNull(missing.MutationVersion);
        Assert.Null(expired.PreviousEntry);
        Assert.NotNull(expired.MutationVersion);
        Assert.Equal([3], keyspace.Get("expired"));
        Assert.Equal(expiry, keyspace.GetEntry("expired")!.ExpiresAtUtc);
    }

    [Fact]
    public void GetAndDelete_WithExistingMissingAndExpiredValues_ReturnsLogicalPreviousValue()
    {
        using var keyspace = Open("get-and-delete");
        DateTimeOffset expiry = DateTimeOffset.UtcNow.AddHours(1);
        long version = keyspace.Put("existing", [1], expiry);
        keyspace.Put("expired", [2], DateTimeOffset.UtcNow.AddMinutes(-1));

        KvExchangeResult deleted = keyspace.GetAndDelete("existing");
        KvExchangeResult missing = keyspace.GetAndDelete("missing");
        KvExchangeResult expired = keyspace.GetAndDelete("expired");

        Assert.NotNull(deleted.PreviousEntry);
        Assert.Equal([1], deleted.PreviousEntry.Value.ToArray());
        Assert.Equal(version, deleted.PreviousEntry.Version);
        Assert.Equal(expiry, deleted.PreviousEntry.ExpiresAtUtc);
        Assert.NotNull(deleted.MutationVersion);
        Assert.Null(keyspace.Get("existing"));
        Assert.Null(missing.PreviousEntry);
        Assert.Null(missing.MutationVersion);
        Assert.Null(expired.PreviousEntry);
        Assert.Null(expired.MutationVersion);
    }

    [Fact]
    public void ConditionalAndExchangeOperations_AfterReopen_ReplayCommittedWalState()
    {
        string path = Path.Combine(_root, "reopen");
        DateTimeOffset expiry = DateTimeOffset.UtcNow.AddHours(1);
        using (var keyspace = OpenAt("reopen", path))
        {
            Assert.True(keyspace.Set("conditional", [1], KvSetCondition.IfNotExists).Applied);
            Assert.False(keyspace.Set("conditional", [9], KvSetCondition.IfNotExists).Applied);
            keyspace.GetAndSet("conditional", [2], expiry);
            keyspace.Put("deleted", [3]);
            Assert.NotNull(keyspace.GetAndDelete("deleted").MutationVersion);
        }

        using var reopened = OpenAt("reopen", path);
        Assert.Equal([2], reopened.Get("conditional"));
        Assert.Equal(expiry, reopened.GetEntry("conditional")!.ExpiresAtUtc);
        Assert.Null(reopened.Get("deleted"));
    }

    [Fact]
    public void Namespace_ConditionalAndExchangeOperations_KeepQualifiedKeyInternal()
    {
        using var keyspace = Open("namespace");
        KvNamespace tenant = keyspace.Namespace("tenant");

        Assert.True(tenant.Set("item", [1], KvSetCondition.IfNotExists).Applied);
        KvExchangeResult exchanged = tenant.GetAndSet("item", [2]);

        Assert.Equal("item", Encoding.UTF8.GetString(exchanged.PreviousEntry!.Key.Span));
        Assert.Equal([2], keyspace.Get("tenant:item"));

        KvExchangeResult deleted = tenant.GetAndDelete("item");
        Assert.Equal("item", Encoding.UTF8.GetString(deleted.PreviousEntry!.Key.Span));
        Assert.Null(keyspace.Get("tenant:item"));
    }

    [Fact]
    public void Namespace_StringOperations_WithOversizedQualifiedKey_RejectBeforeWalAppend()
    {
        KvOptions options = TestOptions() with { MaxKeyBytes = 8 };
        using var keyspace = OpenAt(
            "namespace-key-validation",
            Path.Combine(_root, "namespace-key-validation"),
            options);
        KvNamespace tenant = keyspace.Namespace("ns");
        long walLength = keyspace.ActiveWalLength;

        Assert.Throws<ArgumentOutOfRangeException>(() => tenant.Set("123456", [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => tenant.GetAndSet("123456", [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => tenant.GetAndDelete("123456"));
        Assert.Throws<ArgumentOutOfRangeException>(() => tenant.Set("温度", [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => tenant.GetAndSet("温度", [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => tenant.GetAndDelete("温度"));

        Assert.Equal(walLength, keyspace.ActiveWalLength);
        Assert.Equal(0, keyspace.LastSequence);
    }

    [Fact]
    public void Set_WithInvalidConditionOrOversizeInput_RejectsBeforeWalAppend()
    {
        KvOptions options = TestOptions() with
        {
            MaxKeyBytes = 3,
            MaxValueBytes = 3,
        };
        using var keyspace = OpenAt("validation", Path.Combine(_root, "validation"), options);
        long walLength = keyspace.ActiveWalLength;

        var invalidCondition = Assert.Throws<ArgumentOutOfRangeException>(() =>
            keyspace.Set("key", [1], (KvSetCondition)999));
        Assert.Equal("condition", invalidCondition.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            keyspace.Set(new byte[4], [1], KvSetCondition.IfNotExists));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            keyspace.Set([1], new byte[4], KvSetCondition.IfExists));
        Assert.Throws<ArgumentException>(() =>
            keyspace.Set(
                [1],
                [1],
                KvSetCondition.Always,
                new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.FromHours(8))));

        Assert.Equal(walLength, keyspace.ActiveWalLength);
        Assert.Equal(0, keyspace.LastSequence);
    }

    [Fact]
    public void SetAlways_SyncFailureFaultsKeyspaceAndReopenRecoversUnknownOutcome()
    {
        string path = Path.Combine(_root, "set-sync-failure");
        KvOptions options = TestOptions(syncWalOnEveryWrite: true);
        var expectedError = new InvalidOperationException("simulated set sync failure");
        using (var keyspace = OpenAt("set-sync-failure", path, options))
        {
            keyspace.WalSyncTestHook = () => throw expectedError;

            InvalidOperationException actualError = Assert.Throws<InvalidOperationException>(() =>
                keyspace.Set("committed", [1], KvSetCondition.Always));

            Assert.Same(expectedError, actualError);
            Assert.True(keyspace.IsWriteCommitOutcomeUnknown(actualError));
            Assert.Null(keyspace.Get("committed"));
            Assert.Throws<IOException>(() =>
                keyspace.Set("after-failure", [2], KvSetCondition.Always));
            keyspace.WalSyncTestHook = null;
        }

        using var reopened = OpenAt("set-sync-failure", path, options);
        Assert.Equal([1], reopened.Get("committed"));
        Assert.Null(reopened.Get("after-failure"));
    }

    [Fact]
    public void GetAndSet_SyncFailureFaultsKeyspaceAndReopenRecoversUnknownOutcome()
    {
        string path = Path.Combine(_root, "get-and-set-sync-failure");
        KvOptions options = TestOptions(syncWalOnEveryWrite: true);
        var expectedError = new InvalidOperationException("simulated get-and-set sync failure");
        using (var keyspace = OpenAt("get-and-set-sync-failure", path, options))
        {
            keyspace.Put("item", [1]);
            keyspace.WalSyncTestHook = () => throw expectedError;

            InvalidOperationException actualError = Assert.Throws<InvalidOperationException>(() =>
                keyspace.GetAndSet("item", [2]));

            Assert.Same(expectedError, actualError);
            Assert.True(keyspace.IsWriteCommitOutcomeUnknown(actualError));
            Assert.Equal([1], keyspace.Get("item"));
            Assert.Throws<IOException>(() => keyspace.GetAndSet("blocked", [3]));
            keyspace.WalSyncTestHook = null;
        }

        using var reopened = OpenAt("get-and-set-sync-failure", path, options);
        Assert.Equal([2], reopened.Get("item"));
        Assert.Null(reopened.Get("blocked"));
    }

    [Fact]
    public void GetAndDelete_SyncFailureFaultsKeyspaceAndReopenRecoversUnknownOutcome()
    {
        string path = Path.Combine(_root, "get-and-delete-sync-failure");
        KvOptions options = TestOptions(syncWalOnEveryWrite: true);
        var expectedError = new InvalidOperationException("simulated get-and-delete sync failure");
        using (var keyspace = OpenAt("get-and-delete-sync-failure", path, options))
        {
            keyspace.Put("item", [1]);
            keyspace.WalSyncTestHook = () => throw expectedError;

            InvalidOperationException actualError = Assert.Throws<InvalidOperationException>(() =>
                keyspace.GetAndDelete("item"));

            Assert.Same(expectedError, actualError);
            Assert.True(keyspace.IsWriteCommitOutcomeUnknown(actualError));
            Assert.Equal([1], keyspace.Get("item"));
            Assert.Throws<IOException>(() => keyspace.GetAndDelete("item"));
            keyspace.WalSyncTestHook = null;
        }

        using var reopened = OpenAt("get-and-delete-sync-failure", path, options);
        Assert.Null(reopened.Get("item"));
    }

    [Fact]
    public void StringOperations_WithOversizedAsciiOrUtf8Key_RejectBeforeWalAppend()
    {
        KvOptions options = TestOptions() with { MaxKeyBytes = 3 };
        using var keyspace = OpenAt(
            "string-key-validation",
            Path.Combine(_root, "string-key-validation"),
            options);
        long walLength = keyspace.ActiveWalLength;

        Assert.Throws<ArgumentOutOfRangeException>(() => keyspace.Set("four", [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => keyspace.GetAndSet("four", [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => keyspace.GetAndDelete("four"));
        Assert.Throws<ArgumentOutOfRangeException>(() => keyspace.Set("温度", [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => keyspace.GetAndSet("温度", [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => keyspace.GetAndDelete("温度"));

        Assert.Equal(walLength, keyspace.ActiveWalLength);
        Assert.Equal(0, keyspace.LastSequence);
    }

    [Fact]
    public void ConditionalAndExchangeStringOperations_WithUnpairedSurrogate_RejectBeforeWalAppendWithoutAliasing()
    {
        using var keyspace = Open("invalid-surrogate");
        byte[] replacementKey = [0xef, 0xbf, 0xbd];
        keyspace.Put(replacementKey, [9]);
        long walLength = keyspace.ActiveWalLength;
        long sequence = keyspace.LastSequence;

        foreach (string invalidKey in new[] { "\ud800", "\ud801" })
        {
            Assert.Throws<EncoderFallbackException>(() => keyspace.Set(invalidKey, [1]));
            Assert.Throws<EncoderFallbackException>(() => keyspace.GetAndSet(invalidKey, [2]));
            Assert.Throws<EncoderFallbackException>(() => keyspace.GetAndDelete(invalidKey));
            Assert.Throws<EncoderFallbackException>(() => keyspace.Get(invalidKey));
        }

        Assert.Equal([9], keyspace.Get("\ufffd"));
        Assert.Equal(walLength, keyspace.ActiveWalLength);
        Assert.Equal(sequence, keyspace.LastSequence);
    }

    [Fact]
    public void Namespace_WithUnpairedSurrogateName_RejectsWithoutAliasing()
    {
        using var keyspace = Open("invalid-namespace-name");
        keyspace.Put("\ufffd:item", [9]);
        long walLength = keyspace.ActiveWalLength;
        long sequence = keyspace.LastSequence;

        Assert.Throws<EncoderFallbackException>(() => keyspace.Namespace("\ud800"));
        Assert.Throws<EncoderFallbackException>(() => keyspace.Namespace("\ud801"));

        Assert.Equal([9], keyspace.Get("\ufffd:item"));
        Assert.Equal(walLength, keyspace.ActiveWalLength);
        Assert.Equal(sequence, keyspace.LastSequence);
    }

    [Fact]
    public void NamespaceConditionalAndExchangeOperations_WithUnpairedSurrogateKey_RejectBeforeWalAppendWithoutAliasing()
    {
        using var keyspace = Open("invalid-namespace-key");
        KvNamespace tenant = keyspace.Namespace("tenant");
        tenant.Put("\ufffd", [9]);
        long walLength = keyspace.ActiveWalLength;
        long sequence = keyspace.LastSequence;

        foreach (string invalidKey in new[] { "\ud800", "\ud801" })
        {
            Assert.Throws<EncoderFallbackException>(() => tenant.Set(invalidKey, [1]));
            Assert.Throws<EncoderFallbackException>(() => tenant.GetAndSet(invalidKey, [2]));
            Assert.Throws<EncoderFallbackException>(() => tenant.GetAndDelete(invalidKey));
            Assert.Throws<EncoderFallbackException>(() => tenant.Get(invalidKey));
        }

        Assert.Equal([9], tenant.Get("\ufffd"));
        Assert.Equal(walLength, keyspace.ActiveWalLength);
        Assert.Equal(sequence, keyspace.LastSequence);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private KvKeyspace Open(string name) => OpenAt(name, Path.Combine(_root, name));

    private static KvKeyspace OpenAt(string name, string path, KvOptions? options = null) =>
        KvKeyspace.Open(
            name,
            path,
            options ?? TestOptions());

    private static KvOptions TestOptions(bool syncWalOnEveryWrite = false) =>
        KvOptions.Default with
        {
            AutoCheckpointEnabled = false,
            SyncWalOnEveryWrite = syncWalOnEveryWrite,
            ExpirerEnabled = false,
            CleanupEnabled = false,
        };
}
