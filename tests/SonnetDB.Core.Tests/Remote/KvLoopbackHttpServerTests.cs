using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SonnetDB.Core.Tests.Remote;

/// <summary>验证 loopback 测试宿主关闭与真实处理器故障的边界。</summary>
public sealed class KvLoopbackHttpServerTests
{
    /// <summary>重复关闭空闲监听和刚结束响应的宿主，不泄漏 accept 关闭竞态。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_WithRepeatedShutdown_CompletesWithoutAcceptFailure(bool sendRequest)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            for (int iteration = 0; iteration < 64; iteration++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                await using var server = new KvLoopbackHttpServer(_ => new KvLoopbackResponse(200, "{}"));
                if (sendRequest)
                {
                    using var response = await client.GetAsync(server.Address, deadline.Token);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Single(server.Requests);
                }
            }
        }));
    }

    /// <summary>关闭时仍须向调用者报告处理器的异常，不能将其当作 accept 取消。</summary>
    [Theory]
    [InlineData("socket")]
    [InlineData("disposed")]
    [InlineData("canceled")]
    [InlineData("invalid-data")]
    public async Task DisposeAsync_WithHandlerFailure_PropagatesOriginalException(string failure)
    {
        Exception expected = failure switch
        {
            "socket" => new SocketException((int)SocketError.ConnectionReset),
            "disposed" => new ObjectDisposedException("response-handler"),
            "canceled" => new OperationCanceledException("response-handler"),
            _ => new InvalidDataException("response-handler")
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new KvLoopbackHttpServer(_ =>
        {
            handlerEntered.SetResult();
            throw expected;
        });
        try
        {
            using var connection = new TcpClient();
            await connection.ConnectAsync(IPAddress.Loopback, server.Address.Port, deadline.Token);
            await connection.GetStream().WriteAsync(
                Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"), deadline.Token);
            await handlerEntered.Task.WaitAsync(deadline.Token);
        }
        finally
        {
            Exception? actual = await Record.ExceptionAsync(() => server.DisposeAsync().AsTask());
            Assert.Same(expected, actual);
        }
    }
}
