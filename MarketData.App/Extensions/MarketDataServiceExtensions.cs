using MarketData.App.Parser;
using MarketData.Core.Interfaces;
using MarketData.Infrastructure.Repositories;
using MarketData.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace MarketData.App.Extensions;

public static class MarketDataServiceExtensions
{
    public static void AddMarketDataSystem(this IServiceCollection services, string conn)
    {
        services.AddSingleton<ITickRepository>(new SqlTickRepository(conn));
        services.AddSingleton<MarketDataProcessor>();
        services.AddSingleton<AdapterFactory>();

        var configs = new[] {
            new AdapterSettings("Binance", "ws://localhost:5000/ws", ExchangeParsers.ParseTick),
            new AdapterSettings("ByBit", "ws://localhost:5000/ws", ExchangeParsers.ParseTick),
            new AdapterSettings("OKX", "ws://localhost:5000/ws", ExchangeParsers.ParseTick),
        };

        foreach (var c in configs)
            services.AddSingleton<IExchangeAdapter>(sp => sp.GetRequiredService<AdapterFactory>().Create(c));
    }
}
