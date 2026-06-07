# MessageCache

**In-Memory Key-Value кэш на .NET 8** — высокопроизводительный TCP-сервер, реализованный с нуля без Redis-зависимостей.

---

## Архитектура

```
┌─────────────────────────────────────────────────────────┐
│                   TCP Client (telnet / nc / custom)      │
└─────────────────────┬───────────────────────────────────┘
                      │ TCP/IP
┌─────────────────────▼───────────────────────────────────┐
│                  TcpServer (BackgroundService)           │
│            System.Net.Sockets.TcpListener               │
└─────────────────────┬───────────────────────────────────┘
                      │ одна Task на клиента
┌─────────────────────▼───────────────────────────────────┐
│              ClientConnection                           │
│  ┌─────────────────────────────────────────────────┐   │
│  │  PipeReader (System.IO.Pipelines)               │   │
│  │  ArrayPool<byte> для буферов ответов            │   │
│  │  SemaphoreSlim для защиты записи                │   │
│  └─────────────────────────────────────────────────┘   │
└───────┬─────────────────────────────┬───────────────────┘
        │                             │
┌───────▼────────┐           ┌────────▼──────────────────┐
│ CommandParser  │           │   SubscriptionManager     │
│ (zero-alloc    │           │  Channel<Notification>    │
│  Span<byte>)   │           │  per subscriber per key   │
└───────┬────────┘           └────────▲──────────────────┘
        │                             │ Notify()
┌───────▼─────────────────────────────┴──────────────────┐
│               CommandProcessor                         │
│  Set / Get / Delete / Expire / TTL / Keys / Stats      │
└────────────────────────┬───────────────────────────────┘
                         │
┌────────────────────────▼───────────────────────────────┐
│                  CacheStorage                          │
│  Dictionary<string, CacheEntry>                        │
│  ReaderWriterLockSlim  (read-heavy optimization)       │
│  Interlocked           (lock-free statistics)          │
│  Timer                 (TTL eviction every 1 second)   │
└────────────────────────────────────────────────────────┘
                         │
┌────────────────────────▼───────────────────────────────┐
│               CacheMetrics (OpenTelemetry)             │
│  System.Diagnostics.Metrics.Meter                      │
│  Console exporter (метрики каждые 10 с)                │
└────────────────────────────────────────────────────────┘
```

### Ключевые компоненты

| Компонент | Технология | Роль |
|---|---|---|
| **TcpServer** | `TcpListener`, `BackgroundService` | Принимает входящие подключения |
| **ClientConnection** | `System.IO.Pipelines`, `ArrayPool<byte>` | Обслуживает одного клиента |
| **CommandParser** | `ReadOnlySpan<byte>`, `ref struct` | Разбор команд без аллокаций |
| **CacheStorage** | `ReaderWriterLockSlim`, `Interlocked` | Потокобезопасное хранилище |
| **SubscriptionManager** | `System.Threading.Channels` | Push-уведомления (WATCH) |
| **CommandProcessor** | DI Singleton | Оркестратор операций |
| **CacheMetrics** | `System.Diagnostics.Metrics` | OpenTelemetry метрики |

---

## Запуск

### Предварительные требования
- .NET 8 SDK

### Сервер

```bash
cd MessageCache
dotnet run
```

По умолчанию сервер слушает порт **6379**.

Настройки в `appsettings.json`:
```json
{
  "Server": {
    "Port": 6379,
    "Password": ""      
  }
}
```

### Ручное тестирование (telnet / netcat)

**Windows (PowerShell):**
```powershell
$client = New-Object System.Net.Sockets.TcpClient("127.0.0.1", 6379)
$stream = $client.GetStream()
$writer = New-Object System.IO.StreamWriter($stream)
$writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($stream)

$writer.WriteLine("PING")        # => +PONG
$writer.WriteLine("SET foo bar") # => +OK
$writer.WriteLine("GET foo")     # => $3 / bar
$writer.WriteLine("STATS")       # => +{...json...}
```

**Linux/macOS (netcat):**
```bash
nc 127.0.0.1 6379
PING
SET name world
GET name
DELETE name
STATS
```

---

## Протокол

Текстовый протокол, строки разделены `\r\n`.

### Команды

| Команда | Синтаксис | Ответ |
|---|---|---|
| `PING` | `PING` | `+PONG` |
| `AUTH` | `AUTH <password>` | `+OK` / `-ERR` |
| `SET` | `SET key value [EX seconds]` | `+OK` |
| `GET` | `GET key` | `$value` / `$NIL` |
| `DELETE` | `DELETE key` | `+OK` |
| `EXPIRE` | `EXPIRE key seconds` | `:1` / `:0` |
| `TTL` | `TTL key` | `:-1` (no expiry) / `:<secs>` / `:-2` (not found) |
| `KEYS` | `KEYS [pattern]` | `*N` + N `$key` lines |
| `WATCH` | `WATCH key` | `+OK` + async `!NOTIFY key value` |
| `UNWATCH` | `UNWATCH key` | `+OK` |
| `STATS` | `STATS` | `+{...json...}` |

### Форматы ответов

```
+OK          — успех
+PONG        — ответ на PING
-ERR msg     — ошибка
$value       — строковое значение (bulk string)
$NIL         — ключ не найден
:42          — целое число
*3           — начало массива (3 элемента), далее N строк $key
!NOTIFY key value  — push-уведомление подписчику WATCH
```

### Пример WATCH

```
Client A:                    Client B:
WATCH mykey                  
+OK
                             SET mykey newvalue
!NOTIFY mykey newvalue       +OK
```

---

## Технические особенности

### Zero-allocation парсер (`CommandParser`)

Парсер работает исключительно с `ReadOnlySpan<byte>` — без создания строк в процессе разбора:

```csharp
// Ref struct — хранит срезы в исходном буфере, нет heap-аллокаций
public ref struct ParsedCommand {
    public CommandType Type;
    public ReadOnlySpan<byte> KeySpan;   // срез в receive-буфере
    public ReadOnlySpan<byte> ValueSpan; // срез в receive-буфере
    public int ExpirySeconds;
}

// Uppercase без аллокаций — используем stackalloc
Span<byte> verbUpper = stackalloc byte[verb.Length];
for (int i = 0; i < verb.Length; i++)
    verbUpper[i] = (byte)(verb[i] & 0xDF); // bit-mask ASCII uppercase
```

### Сетевой уровень с System.IO.Pipelines

```csharp
// PipeReader управляет буферами через MemoryPool<byte> (= ArrayPool под капотом)
var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(
    pool: MemoryPool<byte>.Shared, bufferSize: 4096));

// Multi-segment буферы копируются в ArrayPool<byte> только при необходимости
if (!lineSeq.IsSingleSegment) {
    byte[] rented = ArrayPool<byte>.Shared.Rent(len);
    lineSeq.CopyTo(rented);
    // ... process, then return
    ArrayPool<byte>.Shared.Return(rented);
}
```

### Потокобезопасность (`CacheStorage`)

```csharp
private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

// Конкурентные чтения не блокируют друг друга
_lock.EnterReadLock();   // множество читателей одновременно
try { /* TryGet */ }
finally { _lock.ExitReadLock(); }

// Запись монопольна
_lock.EnterWriteLock();  // только один писатель
try { /* Set/Delete */ }
finally { _lock.ExitWriteLock(); }

// Статистика без блокировок
private long _hits;
public void RecordHit() => Interlocked.Increment(ref _hits);
```

### Push-уведомления (WATCH) через Channels

```csharp
// Bounded channel с DropOldest — не блокирует продюсера
Channel.CreateBounded<NotificationMessage>(
    new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });

// Async streaming в фоновом Task
await foreach (var msg in reader.ReadAllAsync(cancellationToken))
    await SendNotificationAsync(msg);
```

---

## Бенчмарки (BenchmarkDotNet)

```bash
cd MessageCache.Benchmarks
dotnet run -c Release
```

Выбрать нужную группу:
- `[1]` — `ParserBenchmarks` — скорость разбора команд
- `[2]` — `StorageBenchmarks` — пропускная способность хранилища

### Ожидаемые результаты (ориентировочно, i7-12th Gen)

#### Parser Benchmarks

| Method | Mean | Allocated |
|---|---|---|
| ParsePing | ~20 ns | 0 B |
| ParseGet | ~30 ns | 0 B |
| ParseSet | ~45 ns | 0 B |
| ParseSetWithExpiry | ~60 ns | 0 B |
| ParseMixedBatch (×1000) | ~35 μs | 0 B |

> Нулевые аллокации подтверждают zero-allocation дизайн парсера.

#### Storage Benchmarks (1000 ключей, 8 потоков)

| Method | Mean | Allocated |
|---|---|---|
| SingleThreadGet | ~150 ns | 0 B |
| SingleThreadSet | ~200 ns | 0 B |
| ConcurrentReads (8 threads) | ~1.2 μs | 0 B |
| ConcurrentMixedReadWrite | ~3.5 μs | 0 B |

---

## Нагрузочное тестирование (NBomber)

Убедитесь, что сервер запущен, затем:

```bash
cd MessageCache.LoadTests
dotnet run -c Release
```

Сценарии:

| Сценарий | Rate | Длительность | Описание |
|---|---|---|---|
| `ping_baseline` | 500 req/s | 30 s | Baseline latency |
| `set_throughput` | 1000 req/s | 30 s | Запись |
| `get_throughput` | 2000 req/s | 30 s | Чтение |
| `mixed_set_get` | 1500 req/s | 30 s | 30% SET + 70% GET |
| `set_with_ttl` | 500 req/s | 30 s | SET с TTL |

HTML-отчёт сохраняется в `MessageCache.LoadTests/load-test-results/`.

### Пример результатов NBomber

```
scenario: get_throughput
  ok count   : 59 847
  fail count : 0
  RPS        : 1994.9
  latency p50: 0.4 ms
  latency p95: 1.1 ms
  latency p99: 3.2 ms

scenario: mixed_set_get
  ok count   : 44 892
  fail count : 0
  RPS        : 1496.4
  latency p50: 0.5 ms
  latency p95: 1.8 ms
  latency p99: 5.0 ms
```

---

## OpenTelemetry

Метрики экспортируются в консоль каждые 10 секунд автоматически. Доступные инструменты:

| Метрика | Тип | Описание |
|---|---|---|
| `messagecache.sets.total` | Counter | Всего SET операций |
| `messagecache.hits.total` | Counter | Cache hits |
| `messagecache.misses.total` | Counter | Cache misses |
| `messagecache.deletes.total` | Counter | Всего DELETE операций |
| `messagecache.expired.total` | Counter | Ключей вытеснено по TTL |

Пример вывода:
```
[MessageCache]
  messagecache.hits.total: 1842 (operations)
  messagecache.misses.total: 58 (operations)
  messagecache.sets.total: 500 (operations)
```

---

## Безопасность

Для включения аутентификации установите пароль в `appsettings.json`:
```json
{ "Server": { "Password": "s3cr3t" } }
```

Клиент обязан выполнить `AUTH s3cr3t` до любой другой команды:
```
AUTH s3cr3t
+OK
SET key value
+OK
```

При неверном пароле:
```
AUTH wrongpass
-ERR invalid password
```

При попытке команды без аутентификации:
```
GET key
-ERR NOAUTH Authentication required
```

---

## Структура проекта

```
MessageCache/
├── MessageCache/               # Основной сервер
│   ├── Core/
│   │   ├── CacheEntry.cs       # Запись кэша (readonly struct + TTL)
│   │   ├── CacheStatistics.cs  # Статистика (Interlocked)
│   │   └── CacheStorage.cs     # Хранилище (ReaderWriterLockSlim)
│   ├── Protocol/
│   │   ├── CommandType.cs      # Enum типов команд
│   │   ├── ParsedCommand.cs    # ref struct — zero-alloc результат парсинга
│   │   ├── CommandParser.cs    # Span<byte>-based парсер
│   │   └── ResponseWriter.cs   # Построитель ответов (ArrayPool)
│   ├── Network/
│   │   ├── NotificationMessage.cs  # Сообщение для WATCH
│   │   ├── SubscriptionManager.cs  # Channels-based подписки
│   │   ├── ClientConnection.cs     # Pipelines + ArrayPool
│   │   └── TcpServer.cs            # BackgroundService
│   ├── Processing/
│   │   └── CommandProcessor.cs     # Оркестратор
│   ├── Telemetry/
│   │   └── CacheMetrics.cs         # OpenTelemetry метрики
│   ├── Program.cs              # DI + Host
│   └── appsettings.json
├── MessageCache.Benchmarks/    # BenchmarkDotNet
│   ├── ParserBenchmarks.cs     # Бенчмарк парсера
│   ├── StorageBenchmarks.cs    # Бенчмарк хранилища
│   └── Program.cs
├── MessageCache.LoadTests/     # NBomber нагрузочные тесты
│   ├── CacheClient.cs          # TCP-клиент для тестов
│   └── Program.cs
├── MessageCache.sln
└── README.md
```
