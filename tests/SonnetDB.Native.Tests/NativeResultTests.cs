using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using SonnetDB.Data;
using SonnetDB.Native;
using Xunit;

namespace SonnetDB.Native.Tests;

public sealed class NativeResultTests
{
    [Fact]
    public void State_TracksReadyRowEndAndFailure()
    {
        var reader = new ProbeReader(1);
        using var result = NativeResult.From(reader);
        Assert.Equal(NativeResultState.Ready, result.State);
        Assert.Equal(1, result.MoveNext());
        Assert.Equal(NativeResultState.Row, result.State);
        Assert.Equal(0, result.MoveNext());
        Assert.Equal(NativeResultState.End, result.State);

        var failure = new IOException("injected state failure");
        using var failed = NativeResult.From(new ProbeReader(1) { ReadFailure = failure });
        Assert.Same(failure, Assert.Throws<IOException>(() => failed.MoveNext()));
        Assert.Equal(NativeResultState.Failed, failed.State);
    }

    [Fact]
    public void NonQuery_StartsReadyAndEndsAfterNext()
    {
        using var result = NativeResult.NonQuery(2);
        Assert.Equal(NativeResultState.Ready, result.State);
        Assert.Equal(2, result.RecordsAffected);
        Assert.Equal(0, result.MoveNext());
        Assert.Equal(NativeResultState.End, result.State);
    }

    [Fact]
    public void From_LargeLogicalResult_DoesNotReadAhead()
    {
        using var reader = new ProbeReader(1_000_000) { ReadLimit = 2 };
        using var result = NativeResult.From(reader);
        Assert.Equal(0, reader.ReadCalls);
        Assert.Equal(2, result.ColumnCount);
        Assert.Equal("DeviceID", Marshal.PtrToStringUTF8(result.GetColumnName(0)));
        Assert.Equal(0, reader.ReadCalls);
        Assert.Equal(1, result.MoveNext());
        Assert.Equal(long.MaxValue, result.GetInt64(0));
        Assert.Equal(1, reader.ReadCalls);
        Assert.Equal(1, result.MoveNext());
        Assert.Equal(long.MaxValue - 1, result.GetInt64(0));
        Assert.Equal(2, reader.ReadCalls);
    }

    [Fact]
    public void MoveNext_AfterEnd_RejectsStaleValuesAndReleasesReader()
    {
        var reader = new ProbeReader(1);
        using var result = NativeResult.From(reader);
        Assert.Equal(1, result.MoveNext());
        Assert.Equal("你好", Marshal.PtrToStringUTF8(result.GetText(1)));
        Assert.Equal(0, result.MoveNext());
        Assert.Throws<InvalidOperationException>(() => result.GetInt64(0));
        Assert.Equal(1, reader.DisposeCalls);
        Assert.Equal(0, result.MoveNext());
        Assert.Equal(2, reader.ReadCalls);
    }

    [Fact]
    public void Dispose_BeforeFirstRow_ReleasesReaderExactlyOnce()
    {
        var reader = new ProbeReader(3);
        var result = NativeResult.From(reader);
        result.Dispose();
        result.Dispose();
        Assert.Equal(0, reader.ReadCalls);
        Assert.Equal(1, reader.DisposeCalls);
        Assert.Throws<ObjectDisposedException>(() => result.MoveNext());
        Assert.Throws<ObjectDisposedException>(() => result.RecordsAffected);
    }

    [Fact]
    public void MoveNext_ReadFailure_RemainsFailureAndReleasesReader()
    {
        var failure = new IOException("injected missing terminal frame");
        var reader = new ProbeReader(3);
        using var result = NativeResult.From(reader);
        Assert.Equal(1, result.MoveNext());
        reader.ReadFailure = failure;
        Assert.Same(failure, Assert.Throws<IOException>(() => result.MoveNext()));
        Assert.Same(failure, Assert.Throws<IOException>(() => result.MoveNext()));
        Assert.Throws<InvalidOperationException>(() => result.GetInt64(0));
        Assert.Equal(1, reader.DisposeCalls);
    }

    [Fact]
    public void Dispose_ReaderCleanupFails_StillReleasesCommandAndOwner()
    {
        var failure = new IOException("reader cleanup");
        var reader = new ProbeReader(1) { DisposeFailure = failure };
        var command = new ProbeCommand();
        int releases = 0;
        var result = NativeResult.From(reader, command, _ => releases++);
        Assert.Same(failure, Assert.Throws<IOException>(result.Dispose));
        result.Dispose();
        Assert.Equal(1, reader.DisposeCalls);
        Assert.Equal(1, command.DisposeCalls);
        Assert.Equal(1, releases);
    }

    [Fact]
    public void MoveNext_ReadAndCleanupFail_PreservesReadFailure()
    {
        var failure = new IOException("missing terminal frame");
        var reader = new ProbeReader(1)
        {
            ReadFailure = failure,
            DisposeFailure = new IOException("reader cleanup"),
        };
        var command = new ProbeCommand();
        using var result = NativeResult.From(reader, command);
        Assert.Same(failure, Assert.Throws<IOException>(() => result.MoveNext()));
        Assert.Same(failure, Assert.Throws<IOException>(() => result.MoveNext()));
        Assert.Equal(1, command.DisposeCalls);
    }

    [Fact]
    public void MoveNext_EmptyResult_ReleasesReaderAndCommand()
    {
        var reader = new ProbeReader(0);
        var command = new ProbeCommand();
        using var result = NativeResult.From(reader, command);
        Assert.Equal(0, result.MoveNext());
        Assert.Equal(0, result.MoveNext());
        Assert.Equal(1, reader.ReadCalls);
        Assert.Equal(1, reader.DisposeCalls);
        Assert.Equal(1, command.DisposeCalls);
        Assert.Equal("DeviceID", Marshal.PtrToStringUTF8(result.GetColumnName(0)));
    }

    [Fact]
    public void MoveNext_CancelledReader_RemainsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new ProbeReader(2);
        using var result = NativeResult.From(reader);
        Assert.Equal(1, result.MoveNext());
        cancellation.Cancel();
        var failure = new OperationCanceledException(cancellation.Token);
        reader.ReadFailure = failure;
        Assert.Same(failure, Assert.Throws<OperationCanceledException>(() => result.MoveNext()));
        Assert.Same(failure, Assert.Throws<OperationCanceledException>(() => result.MoveNext()));
        Assert.Equal(1, reader.DisposeCalls);
    }

    [Fact]
    public void MoveNext_CommandCancelled_ReleasesRealExecutionLease()
    {
        string root = Path.Combine(Path.GetTempPath(), "native-cancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var connection = new SndbConnection(new SndbConnectionStringBuilder { DataSource = root }.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 9223372036854775807 AS DeviceID";
            using var result = NativeResult.From(command.ExecuteReader(), command);
            Assert.Equal(1, result.MoveNext());
            Assert.Equal(long.MaxValue, result.GetInt64(0));
            result.Cancel();
            Assert.Throws<OperationCanceledException>(() => result.MoveNext());
            Assert.Throws<OperationCanceledException>(() => result.MoveNext());
            Assert.Throws<InvalidOperationException>(() => result.GetInt64(0));
            using var next = connection.CreateCommand();
            next.CommandText = "SELECT 2 AS DeviceID";
            Assert.Equal(2L, next.ExecuteScalar());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Connection_ActiveReaders_CloseAndReopenPreservesData()
    {
        string root = Path.Combine(Path.GetTempPath(), "native-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var connection = new NativeConnection(root);
            using (connection.Execute("CREATE TABLE Devices (DeviceID INT, Name STRING, PRIMARY KEY (DeviceID))")) { }
            using var insert = connection.Execute("INSERT INTO Devices (DeviceID, Name) VALUES (9223372036854775807, '你好'), (2, NULL)");
            Assert.Equal(2, insert.RecordsAffected);
            insert.Dispose();
            using var first = connection.Execute("SELECT DeviceID, Name FROM Devices ORDER BY DeviceID DESC");
            using var second = connection.Execute("SELECT DeviceID FROM Devices WHERE DeviceID = 2");
            Assert.Equal(1, first.MoveNext());
            Assert.Equal(long.MaxValue, first.GetInt64(0));
            Assert.Equal("你好", Marshal.PtrToStringUTF8(first.GetText(1)));
            first.Dispose();
            Assert.Equal(1, second.MoveNext());
            Assert.Equal(2, second.GetInt64(0));
            connection.Flush();
            connection.Dispose();
            connection.Dispose();
            Assert.Throws<ObjectDisposedException>(() => second.MoveNext());
            Assert.Throws<ObjectDisposedException>(() => second.RecordsAffected);
            Assert.Throws<ObjectDisposedException>(() => connection.Execute("SELECT 1"));
            using var reopened = new NativeConnection(root);
            using var rows = reopened.Execute("SELECT DeviceID, Name FROM Devices ORDER BY DeviceID DESC");
            Assert.Equal(1, rows.MoveNext());
            Assert.Equal(long.MaxValue, rows.GetInt64(0));
            Assert.Equal(1, rows.MoveNext());
            Assert.Equal(NativeValueType.Null, rows.GetValueType(1));
            Assert.Equal(IntPtr.Zero, rows.GetText(1));
            Assert.Equal(0, rows.MoveNext());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ProbeCommand : DbCommand
    {
        public int DisposeCalls { get; private set; }
        [AllowNull] public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        public override void Cancel() => throw new NotSupportedException();
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) DisposeCalls++; }
    }

    private sealed class ProbeReader(int rowCount) : DbDataReader
    {
        private int _row = -1;
        public int ReadLimit { get; init; } = int.MaxValue;
        public int ReadCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public Exception? ReadFailure { get; set; }
        public Exception? DisposeFailure { get; init; }
        public override int FieldCount => 2;
        public override bool HasRows => rowCount > 0;
        public override bool IsClosed => DisposeCalls > 0;
        public override int RecordsAffected => -1;
        public override int Depth => 0;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
        public override string GetName(int ordinal) => ordinal == 0 ? "DeviceID" : "文本";
        public override int GetOrdinal(string name) => name == "DeviceID" ? 0 : 1;
        public override Type GetFieldType(int ordinal) => ordinal == 0 ? typeof(long) : typeof(string);
        public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;
        public override object GetValue(int ordinal) => ordinal == 0 ? long.MaxValue - _row : "你好";
        public override int GetValues(object[] values)
        {
            int count = Math.Min(values.Length, FieldCount);
            for (int i = 0; i < count; i++) values[i] = GetValue(i);
            return count;
        }
        public override bool Read()
        {
            Assert.False(IsClosed);
            ReadCalls++;
            if (ReadFailure is { } failure) throw failure;
            Assert.True(ReadCalls <= ReadLimit, "Consumer did not request this row.");
            return ++_row < rowCount;
        }
        public override bool NextResult() => false;
        public override bool IsDBNull(int ordinal) => false;
        public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
        public override string GetString(int ordinal) => (string)GetValue(ordinal);
        public override bool GetBoolean(int ordinal) => throw new NotSupportedException();
        public override byte GetByte(int ordinal) => throw new NotSupportedException();
        public override long GetBytes(int ordinal, long offset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override char GetChar(int ordinal) => throw new NotSupportedException();
        public override long GetChars(int ordinal, long offset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();
        public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();
        public override double GetDouble(int ordinal) => throw new NotSupportedException();
        public override float GetFloat(int ordinal) => throw new NotSupportedException();
        public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
        public override short GetInt16(int ordinal) => throw new NotSupportedException();
        public override int GetInt32(int ordinal) => throw new NotSupportedException();
        public override IEnumerator GetEnumerator() => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (!disposing) return;
            DisposeCalls++;
            if (DisposeFailure is { } failure) throw failure;
        }
    }
}
