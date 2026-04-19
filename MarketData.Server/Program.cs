using System.Net.WebSockets;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets();

app.Map("/ws", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        Console.WriteLine("Client connected.");
        var rng = new Random();

        while (webSocket.State == WebSocketState.Open)
        {
            var tick = new
            {
                s = "BTCUSDT",
                p = 60000 + rng.NextDouble() * 100,
                v = rng.NextDouble(),
                t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            var json = System.Text.Json.JsonSerializer.Serialize(tick);
            var bytes = Encoding.UTF8.GetBytes(json);

            await webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);

            await Task.Delay(rng.Next(10, 50));
        }
    }
    else
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
    }
});

app.Run("http://localhost:5000");