using MarketData.Core.Interfaces;
using MarketData.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Net.WebSockets;
using System.Text;

namespace MarketData.Infrastructure.Adapters;

public class WebSocketAdapter : IExchangeAdapter
{
    private readonly Uri _uri;
    private readonly Action<Tick> _callback;
    private readonly Func<dynamic, string, Tick> _parser;
    private readonly ILogger<WebSocketAdapter> _logger;
    public string ExchangeName { get; }

    public WebSocketAdapter(string name,
        string url,
        Action<Tick> callback,
        Func<dynamic, string, Tick> parser,
        ILogger<WebSocketAdapter> logger)
    {
        ExchangeName = name;
        _uri = new Uri(url);
        _callback = callback;
        _parser = parser;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var client = new ClientWebSocket();
            try
            {
                _logger.LogInformation("[Client] {ExchangeName}: Connecting to {uri}...", ExchangeName, _uri);
                await client.ConnectAsync(_uri, ct);
                _logger.LogInformation("[Client] {ExchangeName}: Connected successfully.", ExchangeName);

                var buffer = new byte[1024 * 4];

                while (client.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                    if (result.MessageType == WebSocketMessageType.Close) break;

                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    dynamic rawData = JsonConvert.DeserializeObject(json)!;

                    Tick normalizedTick = _parser(rawData, ExchangeName);

                    _callback(normalizedTick);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("{ExchangeName} Connection lost: {Message}. Reconnecting...", ExchangeName, ex.Message);
                await Task.Delay(3000, ct);
            }
        }
    }
}
