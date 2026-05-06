# Code Review Report — MarketDataSystem

**Дата:** 24 апреля 2026  
**Задание:** Система агрегации и обработки данных с биржевых торгов  
**Целевая нагрузка:** 50–100 тиков/сек, 2–3 источника

---

## Оценка уровня кандидата

**Junior+** / слабый Middle.

Кандидат понимает базовые концепции: использует `Channel<T>` для разделения producer/consumer, `ConcurrentDictionary` для потокобезопасности, батчевую запись в БД, структурированное логирование. Однако несколько критичных ошибок (молчаливая потеря данных, неполная обработка WebSocket-фреймов, поглощение `OperationCanceledException`, SQL N+1) говорят о том, что производственный опыт с высоконагруженными системами отсутствует. Архитектура слоёв выдержана, SOLID принципы соблюдены формально.

---

## Топ-10 проблем (в порядке убывания критичности)

---

### №1 — КРИТИЧНО | WebSocket: игнорирование фрагментации сообщений

**Файл:** `MarketData.Infrastructure/Adapters/WebSocketAdapter.cs`

WebSocket-протокол допускает фрагментацию сообщений: одно логическое сообщение может прийти в нескольких фреймах. Буфер зафиксирован на 4 КБ. Флаг `result.EndOfMessage` никогда не проверяется. Если JSON-сообщение превысит 4 КБ или будет фрагментировано сервером, строка `json` окажется обрезанной; `JsonConvert.DeserializeObject` бросит исключение или вернёт некорректные данные.

```csharp
var buffer = new byte[1024 * 4];

while (client.State == WebSocketState.Open && !ct.IsCancellationRequested)
{
    var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

    if (result.MessageType == WebSocketMessageType.Close) break;

    // ❌ result.EndOfMessage не проверяется
    // ❌ Если сообщение > 4096 байт — данные молча обрезаются
    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
    dynamic rawData = JsonConvert.DeserializeObject(json)!;
```

---

### №2 — КРИТИЧНО | Молчаливая потеря тиков при переполнении канала

**Файл:** `MarketData.Processing/MarketDataProcessor.cs` + `MarketData.App/Program.cs`

`Channel<Tick>` создаётся с ограничением 10 000 элементов. Callback-продюсер использует `TryWrite`, который при заполненном канале возвращает `false` и **молча выбрасывает тик**. Никакого логирования, никакого счётчика потерь, никакого backpressure. При пике нагрузки или медленном consumer (например, при задержке БД) данные теряются незаметно.

```csharp
// MarketDataProcessor.cs
private readonly Channel<Tick> _queue = Channel.CreateBounded<Tick>(10000);

public Action<Tick> GetProducerCallback() => t => _queue.Writer.TryWrite(t);
//                                                          ^^^^^^^^
// ❌ возвращает false при заполнении — тик теряется без следа

// Program.cs — три адаптера пишут в один канал без backpressure
var tasks = adapters.Select(a => Task.Run(() => a.StartAsync(cts.Token))).ToList();
```

---

### №3 — КРИТИЧНО | Незаписанный остаток batch при завершении

**Файл:** `MarketData.Processing/MarketDataProcessor.cs`

Когда `CancellationToken` отменяется, `ReadAllAsync(ct)` бросает `OperationCanceledException`, и метод завершается. Локальный список `batch` может содержать от 1 до 99 тиков, которые никогда не будут записаны в БД — данные безвозвратно теряются.

```csharp
public async Task RunConsumerAsync(CancellationToken ct)
{
    var batch = new List<Tick>();
    await foreach (var tick in _queue.Reader.ReadAllAsync(ct))
    {
        // ...
        batch.Add(tick);

        if (batch.Count >= WriteBatchSize)
        {
            await _repo.AddTicksBatchAsync(batch);
            batch.Clear();
        }
    }
    // ❌ Сюда код никогда не доходит при отмене через CT
    // ❌ Остаток в batch (до 99 тиков) теряется
}
```

---

### №4 — ВЫСОКИЙ | Поглощение OperationCanceledException в WebSocket-адаптере

**Файл:** `MarketData.Infrastructure/Adapters/WebSocketAdapter.cs`

Голый `catch (Exception ex)` перехватывает в том числе `OperationCanceledException`, порождаемый при отмене токена на `ConnectAsync`, `ReceiveAsync` или `Task.Delay`. Вместо корректного завершения адаптер логирует отмену как ошибку соединения и пытается переподключиться. Это задерживает graceful shutdown и засоряет логи ложными ошибками.

```csharp
catch (Exception ex)  // ❌ перехватывает и OperationCanceledException
{
    _logger.LogError("{ExchangeName} Connection lost: {Message}. Reconnecting...",
        ExchangeName, ex.Message);
    // ❌ Лог-уровень Error для штатной отмены
    await Task.Delay(3000, ct);  // бросит OCE снова → поймается снова
}
```

---

### №5 — ВЫСОКИЙ | SQL N+1: Dapper ExecuteAsync выполняет INSERT поштучно

**Файл:** `MarketData.Infrastructure/Repositories/SqlTickRepository.cs`

`Dapper.ExecuteAsync(sql, IEnumerable<T>)` итерирует коллекцию и выполняет отдельный `INSERT` на каждый элемент в рамках одного открытого соединения. Это не bulk-вставка — каждый тик получает отдельный round-trip к SQL Server. При батче из 100 записей — 100 последовательных INSERT вместо одного. При целевой нагрузке 100 тиков/сек и задержке БД ≥10 мс потребитель не успевает разгрести канал.

```csharp
public async Task AddTicksBatchAsync(IEnumerable<Tick> ticks)
{
    using var conn = new SqlConnection(_connectionString);
    const string sql = "INSERT INTO Ticks (Exchange, Symbol, Price, Volume, Timestamp) " +
                       "VALUES (@Exchange, @Symbol, @Price, @Volume, @Timestamp)";
    // ❌ Dapper выполняет N отдельных INSERT, а не один bulk-insert
    await conn.ExecuteAsync(sql, ticks);
}
```

---

### №6 — ВЫСОКИЙ | `dynamic` + Newtonsoft.Json в hot path: GC pressure

**Файл:** `MarketData.Infrastructure/Adapters/WebSocketAdapter.cs`, `MarketData.App/Parser/ExchangeParsers.cs`

`JsonConvert.DeserializeObject(json)` без параметра типа возвращает `JObject`. Каждый разбор создаёт `JObject` с полным графом `JToken`-объектов. Вдобавок `dynamic`-доступ к свойствам (`d.s`, `d.p`, `d.v`, `d.t`) использует рефлексию и DLR-биндинг при каждом вызове. При 100 вызовах/сек это стабильная GC-нагрузка от объектов краткого времени жизни. Нет никакой обработки ошибок парсинга — `NullReferenceException` или `RuntimeBinderException` при любом отклонении формата.

```csharp
// WebSocketAdapter.cs
dynamic rawData = JsonConvert.DeserializeObject(json)!;  // ❌ JObject + dynamic
Tick normalizedTick = _parser(rawData, ExchangeName);    // ❌ DLR-биндинг при каждом тике

// ExchangeParsers.cs
public static Tick ParseTick(dynamic d, string ex) =>
    new Tick(ex, (string)d.s, (decimal)d.p, (decimal)d.v,   // ❌ нет try/catch
             DateTimeOffset.FromUnixTimeMilliseconds((long)d.t).UtcDateTime);
```

---

### №7 — ВЫСОКИЙ | Логирование дубликатов в потребительском hot path

**Файл:** `MarketData.Processing/MarketDataProcessor.cs`

Каждый дублирующийся тик вызывает `LogInformation` с форматированием строки-ключа. При агрессивной дедупликации (например, все три адаптера шлют одинаковые тики с одного сервера, как в текущей конфигурации) это означает 200+ лог-записей/сек. Структурированное логирование всё равно аллоцирует `object[]` для параметров. Это одновременно засоряет логи и создаёт GC pressure прямо в critical path обработки.

```csharp
else
{
    // ❌ LogInformation на каждый дубликат в hot path
    // ❌ tickKey — строковая аллокация уже произошла выше
    _logger.LogInformation("Duplicate missed: {Key}", tickKey);
    continue;
}
```

---

### №8 — ВЫСОКИЙ | Строковая аллокация ключа дедупликации на каждый тик

**Файл:** `MarketData.Processing/MarketDataProcessor.cs`

На каждый принятый тик (100/сек) в consumer hot path создаётся новая строка через интерполяцию. `decimal.ToString()` и `long.ToString()` внутри интерполяции дополнительно аллоцируют промежуточные строки. Все эти объекты — краткоживущий мусор Gen0, при 100 тиках/сек это несколько тысяч аллокаций/сек только на ключи.

```csharp
// ❌ Новая строка + неявные ToString() для decimal и long на каждый тик
string tickKey = $"{tick.Exchange}_{tick.Symbol}_{tick.Price}_{tick.Timestamp.Ticks}";

if (_recentTicks.TryAdd(tickKey, 0))
{
    _keysOrder.Enqueue(tickKey); // ещё одна ссылка на ту же строку в очереди
```

---

### №9 — СРЕДНИЙ | Логическая несвязность двухструктурного кэша дедупликации

**Файл:** `MarketData.Processing/MarketDataProcessor.cs`

Механизм вытеснения использует две несвязанные структуры: `ConcurrentDictionary<string, byte>` и `ConcurrentQueue<string>`. Они обновляются отдельными, не атомарными операциями. Если ключ присутствует в `_recentTicks`, но уже вытеснен из `_keysOrder` (или наоборот), инварианты кэша нарушаются: тик может быть признан дубликатом, хотя его ключ фактически устарел, или ключ будет продолжать занимать память в словаре после «вытеснения».

```csharp
private readonly ConcurrentQueue<string> _keysOrder = new();
private readonly ConcurrentDictionary<string, byte> _recentTicks = new();

// ❌ TryAdd в словарь и Enqueue в очередь — не атомарная операция
if (_recentTicks.TryAdd(tickKey, 0))
{
    _keysOrder.Enqueue(tickKey);

    // ❌ Count на ConcurrentQueue не гарантирует консистентность с последующим TryDequeue
    if (_keysOrder.Count > MaxCacheSize)
    {
        for (int i = 0; i < EvictionBatchSize; i++)
        {
            if (_keysOrder.TryDequeue(out var oldKey))
                _recentTicks.TryRemove(oldKey, out _);  // ❌ может удалить «живой» ключ
```

---

### №10 — СРЕДНИЙ | Незакрытое WebSocket-соединение при нормальном завершении

**Файл:** `MarketData.Infrastructure/Adapters/WebSocketAdapter.cs`

Когда внутренний цикл завершается по `result.MessageType == WebSocketMessageType.Close` или сервер инициирует закрытие, адаптер выходит из цикла без отправки WebSocket Close-фрейма в ответ (`CloseAsync`). По протоколу WebSocket (RFC 6455) получатель Close-фрейма обязан ответить симметричным Close-фреймом. Игнорирование этого приводит к тому, что `using var client` вызывает `Dispose` на незакрытом соединении — сервер может получить `AbnormalClosure` вместо корректного завершения.

```csharp
while (client.State == WebSocketState.Open && !ct.IsCancellationRequested)
{
    var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

    // ❌ При получении Close-фрейма просто выходим — без CloseAsync в ответ
    if (result.MessageType == WebSocketMessageType.Close) break;
    // ...
}
// ❌ using var client → Dispose без предшествующего CloseAsync
```

---

## Сводная таблица

| # | Уровень | Файл | Суть |
|---|---------|------|------|
| 1 | 🔴 КРИТИЧНО | `WebSocketAdapter.cs` | Нет сборки фрагментированных WS-сообщений, буфер 4 КБ |
| 2 | 🔴 КРИТИЧНО | `MarketDataProcessor.cs` | TryWrite молча дропает тики при переполнении канала |
| 3 | 🔴 КРИТИЧНО | `MarketDataProcessor.cs` | Остаток batch не сбрасывается при завершении |
| 4 | 🟠 ВЫСОКИЙ | `WebSocketAdapter.cs` | catch(Exception) поглощает OperationCanceledException |
| 5 | 🟠 ВЫСОКИЙ | `SqlTickRepository.cs` | Dapper N+1 INSERT вместо bulk-вставки |
| 6 | 🟠 ВЫСОКИЙ | `WebSocketAdapter.cs`, `ExchangeParsers.cs` | dynamic + Newtonsoft.Json в hot path, нет обработки ошибок |
| 7 | 🟠 ВЫСОКИЙ | `MarketDataProcessor.cs` | LogInformation на каждый дубликат в hot path |
| 8 | 🟠 ВЫСОКИЙ | `MarketDataProcessor.cs` | Строковая аллокация ключа на каждый тик |
| 9 | 🟡 СРЕДНИЙ | `MarketDataProcessor.cs` | Несвязный двухструктурный кэш дедупликации |
| 10 | 🟡 СРЕДНИЙ | `WebSocketAdapter.cs` | WS Close-рукопожатие не завершается корректно |
