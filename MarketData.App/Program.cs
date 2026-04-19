using MarketData.App.Extensions;
using MarketData.Core.Interfaces;
using MarketData.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole());
services.AddMarketDataSystem("Server=.\\sqlexpress;Database=MarketDataDB;Trusted_Connection=True;TrustServerCertificate=True;");

var sp = services.BuildServiceProvider();
using var cts = new CancellationTokenSource();

var proc = sp.GetRequiredService<MarketDataProcessor>();
var adapters = sp.GetServices<IExchangeAdapter>();

var tasks = adapters.Select(a => Task.Run(() => a.StartAsync(cts.Token))).ToList();
tasks.Add(Task.Run(() => proc.RunConsumerAsync(cts.Token)));

_ = Task.Run(async () => {
    while (!cts.Token.IsCancellationRequested)
    {
        Console.Title = $"Ticks: {proc.TotalProcessed}";
        await Task.Delay(1000);
    }
});

Console.WriteLine("Press ESC to stop.");
while (!(Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)) await Task.Delay(100);

cts.Cancel();
await Task.WhenAll(tasks);