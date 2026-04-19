namespace MarketData.Core.Models;

public record Tick(string Exchange, string Symbol, decimal Price, decimal Volume, DateTime Timestamp);
