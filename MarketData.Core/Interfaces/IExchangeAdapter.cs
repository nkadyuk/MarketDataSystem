namespace MarketData.Core.Interfaces;

public interface IExchangeAdapter
{
    string ExchangeName { get; }
    Task StartAsync(CancellationToken ct);
}
