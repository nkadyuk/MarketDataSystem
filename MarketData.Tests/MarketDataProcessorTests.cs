using MarketData.Core.Interfaces;
using MarketData.Core.Models;
using MarketData.Processing;
using Microsoft.Extensions.Logging;
using Moq;

namespace MarketData.Tests;

public class MarketDataProcessorTests
{
    private readonly Mock<ITickRepository> _repoMock;
    private readonly Mock<ILogger<MarketDataProcessor>> _loggerMock;
    private readonly MarketDataProcessor _processor;

    public MarketDataProcessorTests()
    {
        _repoMock = new Mock<ITickRepository>();
        _loggerMock = new Mock<ILogger<MarketDataProcessor>>();
        _processor = new MarketDataProcessor(_repoMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task Should_NotSaveToDb_When_BatchIsNotFull()
    {
        // Arrange
        var callback = _processor.GetProducerCallback();
        var tick = new Tick("Binance", "BTC", 60000, 1, DateTime.UtcNow);

        // Act
        callback(tick);

        var cts = new CancellationTokenSource(500);
        await _processor.RunConsumerAsync(cts.Token).ContinueWith(_ => { });

        // Assert
        _repoMock.Verify(r => r.AddTicksBatchAsync(It.IsAny<IEnumerable<Tick>>()), Times.Never);
    }

    [Fact]
    public async Task Should_FilterDuplicates()
    {
        // Arrange
        var callback = _processor.GetProducerCallback();
        var tick = new Tick("Binance", "BTC", 60000, 1, DateTime.UtcNow);

        // Act
        for (int i = 0; i < 200; i++) callback(tick);

        var cts = new CancellationTokenSource(1000);
        await _processor.RunConsumerAsync(cts.Token).ContinueWith(_ => { });

        // Assert
        _repoMock.Verify(r => r.AddTicksBatchAsync(It.IsAny<IEnumerable<Tick>>()), Times.Never);
        Assert.Equal(0, _processor.TotalProcessed);
    }

    [Fact]
    public async Task Should_SaveToDb_When_BatchIsReached()
    {
        // Arrange
        var capturedTicks = new List<Tick>();

        _repoMock
            .Setup(r => r.AddTicksBatchAsync(It.IsAny<IEnumerable<Tick>>()))
            .Callback<IEnumerable<Tick>>(input => capturedTicks.AddRange(input))
            .Returns(Task.CompletedTask);

        var callback = _processor.GetProducerCallback();
        for (int i = 0; i < 100; i++)
        {
            callback(new Tick("Ex", "BTC", 60000 + i, 1, DateTime.UtcNow.AddMilliseconds(i)));
        }

        // Act
        using var cts = new CancellationTokenSource(1000);
        var consumerTask = _processor.RunConsumerAsync(cts.Token);

        await Task.Delay(200);
        cts.Cancel();
        try { await consumerTask; } catch (OperationCanceledException) { }

        // Assert
        Assert.Equal(100, capturedTicks.Count);

        _repoMock.Verify(r => r.AddTicksBatchAsync(It.IsAny<IEnumerable<Tick>>()), Times.Once);
    }
}