using System.Data;
using System.Data.Common;
using SonnetDB.Data;
using SonnetDB.Data.Internal;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Core.Tests.Ado;

public sealed class NativeDateTime2ParameterTests
{
    [Fact]
    public void RemoteParameters_MixedPlaceholderStyles_RejectsBeforePreparingRequest()
    {
        var parameters = new SndbParameterCollection();
        parameters.Add(new SndbParameter("stamp", DateTime.UnixEpoch.AddTicks(1)));
        string sql = "SELECT @stamp,?";
        Assert.Throws<NotSupportedException>(() => ParameterBinder.PrepareRemoteRequest(ref sql, parameters));
    }

    [Fact]
    public void RemoteParameters_CallerMutationAfterPreparation_CannotChangeFrozenBytes()
    {
        DateTime original = DateTime.UnixEpoch.AddTicks(1);
        byte[] blob = [1, 2, 3];
        var parameters = new SndbParameterCollection();
        parameters.Add(new SndbParameter("stamp", original));
        parameters.Add(new SndbParameter("blob", blob));
        string sql = "SELECT @stamp,@blob";
        byte[] payload = Assert.IsType<byte[]>(ParameterBinder.PrepareRemoteRequest(ref sql, parameters));
        parameters[0].Value = original.AddTicks(1);
        blob[0] = 9;
        var request = SqlExecuteFrameCodec.DecodeRequest(payload);
        Assert.Equal(original, request.Parameters[0].Value);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(request.Parameters[1].Value));
        Assert.Equal(SqlFrameParameterKind.DateTime, request.Parameters[0].Kind);
    }

    [Theory]
    [InlineData(DbType.Object)]
    [InlineData(DbType.DateTime2)]
    public void Parameters_FullTicksAndNull_DeclareDateTime2WithoutScaleOptIn(DbType type)
    {
        string root = Path.Combine(Path.GetTempPath(), "issue236-ado-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var connection = new SndbConnection($"Data Source={root}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE precise(id INT,Stamp DATETIME2 NULL,PRIMARY KEY(id))";
            command.ExecuteNonQuery();
            DataRow declared = Assert.Single(connection.GetSchema("DataTypes").Rows.Cast<DataRow>(),
                static row => (string)row["TypeName"] == "DATETIME2");
            Assert.Equal((int)DbType.DateTime2, declared["ProviderDbType"]);
            Assert.Equal((short)7, declared["MinimumScale"]);
            Assert.Equal((short)7, declared["MaximumScale"]);
            command.Parameters.Add(new SndbParameter("stamp", DateTime.UnixEpoch.AddTicks(1)) { DbType = type });
            command.CommandText = "INSERT INTO precise(id,Stamp) VALUES(1,@stamp) RETURNING Stamp";
            Assert.Equal(DateTime.UnixEpoch.AddTicks(1), command.ExecuteScalar());
            command.Parameters[0].DbType = DbType.DateTime2;
            command.Parameters[0].Value = DBNull.Value;
            foreach (string sql in new[] { "SELECT Stamp,@stamp FROM precise LIMIT 0", "SELECT Stamp,@stamp FROM precise WHERE FALSE" })
            {
                command.CommandText = sql;
                using var reader = command.ExecuteReader();
                for (int column = 0; column < 2; column++)
                {
                    Assert.Equal(typeof(DateTime), reader.GetFieldType(column));
                    Assert.Equal("DateTime2", reader.GetDataTypeName(column));
                    Assert.Equal(7, reader.GetColumnSchema()[column].NumericScale);
                    Assert.Equal((int)DbType.DateTime2, reader.GetSchemaTable()!.Rows[column][SchemaTableColumn.ProviderType]);
                }
                Assert.False(reader.Read());
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void LegacyDateTime_SubMillisecondParameter_RejectsBeforeWrite()
    {
        string root = Path.Combine(Path.GetTempPath(), "issue236-legacy-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var connection = new SndbConnection($"Data Source={root}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE legacy(id INT,Stamp DATETIME,PRIMARY KEY(id))";
            command.ExecuteNonQuery();
            command.Parameters.Add(new SndbParameter("stamp", DateTime.UnixEpoch.AddTicks(1)) { DbType = DbType.DateTime });
            command.CommandText = "INSERT INTO legacy(id,Stamp) VALUES(1,@stamp)";
            Assert.Throws<ArgumentException>(() => command.ExecuteNonQuery());
            command.Parameters[0].Value = DateTime.UnixEpoch;
            Assert.Equal(1, command.ExecuteNonQuery());
            command.CommandText = "SELECT Stamp FROM legacy";
            Assert.Equal(DateTime.UnixEpoch, command.ExecuteScalar());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
