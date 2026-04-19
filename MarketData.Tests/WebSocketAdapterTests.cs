using MarketData.App.Parser;
using MarketData.Core.Models;
using MarketData.Infrastructure.Adapters;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using System.Net.WebSockets;

namespace MarketData.Tests;

public class WebSocketAdapterTests
{
    private readonly string _webSocketName = "Test";
    private readonly string _connectionLostMessage = "Connection lost";
    private readonly string _connectionSuccessMessage = "Connected successfully";

    [Fact]
    public async Task Adapter_Should_LogWarning_On_Connection_Error()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<WebSocketAdapter>>();

        var adapter = new WebSocketAdapter(
            _webSocketName,
            "ws://invalid-url",
            (tick) => { },
            ExchangeParsers.ParseTick,
            loggerMock.Object);

        using var cts = new CancellationTokenSource();

        // Act
        var task = adapter.StartAsync(cts.Token);
        await Task.Delay(200);
        cts.Cancel();

        // Assert
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains(_connectionLostMessage)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartAsync_Should_LogInformation_On_SuccessfulConnection()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<WebSocketAdapter>>();
        string testUrl = "ws://localhost:8080/test/";

        using var server = new HttpListener();
        server.Prefixes.Add("http://localhost:8080/test/");
        server.Start();

        var serverTask = Task.Run(async () =>
        {
            var context = await server.GetContextAsync();
            if (context.Request.IsWebSocketRequest)
            {
                var wsContext = await context.AcceptWebSocketAsync(null);

                await Task.Delay(1000);
                await wsContext.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Test end", CancellationToken.None);
            }
        });

        var adapter = new WebSocketAdapter(
            _webSocketName,
            testUrl,
            (tick) => { },
            ExchangeParsers.ParseTick,
            loggerMock.Object);

        using var cts = new CancellationTokenSource();

        // Act
        var adapterTask = adapter.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();

        // Assert
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains(_connectionSuccessMessage)),
                null,
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce);

        server.Stop();
    }
}
