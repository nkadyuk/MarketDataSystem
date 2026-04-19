using MarketData.Core.Interfaces;
using MarketData.Core.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace MarketData.Processing;

public class MarketDataProcessor
{
    private readonly Channel<Tick> _queue = Channel.CreateBounded<Tick>(10000);
    private readonly ITickRepository _repo;
    private readonly ILogger<MarketDataProcessor> _logger;

    private const int WriteBatchSize = 100;
    private const int EvictionBatchSize = 1000;
    private const int MaxCacheSize = 10000;

    private long _totalProcessed = 0;

    private readonly ConcurrentQueue<string> _keysOrder = new();
    private readonly ConcurrentDictionary<string, byte> _recentTicks = new();

    public long TotalProcessed => Interlocked.Read(ref _totalProcessed);


    public MarketDataProcessor(ITickRepository repo, ILogger<MarketDataProcessor> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public Action<Tick> GetProducerCallback() => t => _queue.Writer.TryWrite(t);

    public async Task RunConsumerAsync(CancellationToken ct)
    {
        var batch = new List<Tick>();
        await foreach (var tick in _queue.Reader.ReadAllAsync(ct))
        {
            string tickKey = $"{tick.Exchange}_{tick.Symbol}_{tick.Price}_{tick.Timestamp.Ticks}";

            if (_recentTicks.TryAdd(tickKey, 0))
            {
                _keysOrder.Enqueue(tickKey);

                if (_keysOrder.Count > MaxCacheSize)
                {
                    _logger.LogInformation("Cache limit reached. Clearing {Count} old entries...", EvictionBatchSize);

                    for (int i = 0; i < EvictionBatchSize; i++)
                    {
                        if (_keysOrder.TryDequeue(out var oldKey))
                        {
                            _recentTicks.TryRemove(oldKey, out _);
                        }
                        else break;
                    }
                }

                batch.Add(tick);
            }
            else
            {
                _logger.LogInformation("Duplicate missed: {Key}", tickKey);
                continue;
            }

            if (batch.Count >= WriteBatchSize)
            {
                await _repo.AddTicksBatchAsync(batch);
                _logger.LogInformation("[DB] Saved batch of {Count} records", batch.Count);

                Interlocked.Add(ref _totalProcessed, batch.Count);

                batch.Clear();
            }
        }
    }
}