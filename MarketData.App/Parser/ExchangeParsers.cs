using MarketData.Core.Models;

namespace MarketData.App.Parser;

public static class ExchangeParsers
{
    public static Tick ParseTick(dynamic d, string ex) =>
        new Tick(ex, (string)d.s, (decimal)d.p, (decimal)d.v,
                 DateTimeOffset.FromUnixTimeMilliseconds((long)d.t).UtcDateTime);
}
