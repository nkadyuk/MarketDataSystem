using MarketData.Core.Interfaces;
using MarketData.Core.Models;
using MarketData.Infrastructure.Adapters;
using MarketData.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarketData.App.Extensions;

public record AdapterSettings(string Name, string Url, Func<dynamic, string, Tick> Parser);

public class AdapterFactory
{
    private readonly IServiceProvider _sp;
    public AdapterFactory(IServiceProvider sp) => _sp = sp;

    public IExchangeAdapter Create(AdapterSettings s)
    {
        return new WebSocketAdapter(
            s.Name, s.Url,
            _sp.GetRequiredService<MarketDataProcessor>().GetProducerCallback(),
            s.Parser,
            _sp.GetRequiredService<ILogger<WebSocketAdapter>>()
        );
    }
}