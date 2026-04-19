using MarketData.Core.Models;

namespace MarketData.Core.Interfaces;

public interface ITickRepository
{
    Task AddTicksBatchAsync(IEnumerable<Tick> ticks);
}
