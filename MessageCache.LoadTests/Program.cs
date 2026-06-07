using MessageCache.LoadTests;
using NBomber.CSharp;

const string host = "127.0.0.1";
const int port = 6379;
const int durationSeconds = 30;
const int warmupSeconds = 5;

const int smallPool = 30;
const int largePool = 80;

Console.WriteLine("MessageCache NBomber Load Tests");
Console.WriteLine($"Target: {host}:{port}");
Console.WriteLine();
Console.WriteLine("Убедитесь что сервер запущен на порту 6379.");
Console.WriteLine("Нажмите любую кнопку для старта...");
Console.ReadKey(intercept: true);
Console.WriteLine();

// ── Проверка доступности сервера ──────────────────────────────────────────────
const string readKey = "loadtest:read:stable";
try
{
    using var seedClient = new CacheClient(host, port);
    seedClient.Set(readKey, "stable-value");
    Console.WriteLine($"Сервер доступен. Seed-ключ '{readKey}' записан.");
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"ОШИБКА: Нет подключения к серверу {host}:{port}");
    Console.WriteLine(ex.Message);
    Console.ResetColor();
    return;
}

var pingPool    = new ConnectionPool(host, port, smallPool);
var setPool     = new ConnectionPool(host, port, largePool);
var getPool     = new ConnectionPool(host, port, largePool);
var mixedPool   = new ConnectionPool(host, port, largePool);
var ttlPool     = new ConnectionPool(host, port, smallPool);

Console.WriteLine($"Пулы соединений созданы: ping={smallPool}, set/get/mixed={largePool}, ttl={smallPool}");
Console.WriteLine();

var pingScenario = Scenario.Create("ping_baseline", async ctx =>
    {
        var client = pingPool.Rent();
        try
        {
            var response = client.Ping();
            pingPool.Return(client);
            return response == "+PONG"
                ? Response.Ok()
                : Response.Fail(message: $"unexpected: {response}");
        }
        catch (Exception ex)
        {
            pingPool.Return(client, discard: true); // broken — не возвращаем в пул
            return Response.Fail(message: ex.Message);
        }
    })
    .WithWarmUpDuration(TimeSpan.FromSeconds(warmupSeconds))
    .WithLoadSimulations(
        Simulation.Inject(rate: 500, interval: TimeSpan.FromSeconds(1),
            during: TimeSpan.FromSeconds(durationSeconds))
    );

var setScenario = Scenario.Create("set_throughput", async ctx =>
    {
        var client = setPool.Rent();
        try
        {
            string key = $"loadtest:{ctx.ScenarioInfo.InstanceNumber}:{ctx.InvocationNumber}";
            var response = client.Set(key, "value-" + ctx.InvocationNumber);
            setPool.Return(client);
            return response == "+OK"
                ? Response.Ok()
                : Response.Fail(message: $"unexpected: {response}");
        }
        catch (Exception ex)
        {
            setPool.Return(client, discard: true);
            return Response.Fail(message: ex.Message);
        }
    })
    .WithWarmUpDuration(TimeSpan.FromSeconds(warmupSeconds))
    .WithLoadSimulations(
        Simulation.Inject(rate: 1000, interval: TimeSpan.FromSeconds(1),
            during: TimeSpan.FromSeconds(durationSeconds))
    );

var getScenario = Scenario.Create("get_throughput", async ctx =>
    {
        var client = getPool.Rent();
        try
        {
            var response = client.Get(readKey);
            getPool.Return(client);
            return response is not null && response.StartsWith("$")
                ? Response.Ok()
                : Response.Fail(message: $"unexpected: {response}");
        }
        catch (Exception ex)
        {
            getPool.Return(client, discard: true);
            return Response.Fail(message: ex.Message);
        }
    })
    .WithWarmUpDuration(TimeSpan.FromSeconds(warmupSeconds))
    .WithLoadSimulations(
        Simulation.Inject(rate: 2000, interval: TimeSpan.FromSeconds(1),
            during: TimeSpan.FromSeconds(durationSeconds))
    );

var mixedScenario = Scenario.Create("mixed_set_get", async ctx =>
    {
        var client = mixedPool.Rent();
        try
        {
            string key = $"mixed:{ctx.InvocationNumber % 100}";
            string statusCode;
            bool ok;

            if (ctx.InvocationNumber % 10 < 3)
            {
                var r = client.Set(key, $"val-{ctx.InvocationNumber}");
                ok = r == "+OK";
                statusCode = "SET";
            }
            else
            {
                var r = client.Get(key);
                ok = r is not null;
                statusCode = "GET";
            }

            mixedPool.Return(client);
            return ok ? Response.Ok(statusCode: statusCode) : Response.Fail(statusCode: statusCode);
        }
        catch (Exception ex)
        {
            mixedPool.Return(client, discard: true);
            return Response.Fail(message: ex.Message);
        }
    })
    .WithWarmUpDuration(TimeSpan.FromSeconds(warmupSeconds))
    .WithLoadSimulations(
        Simulation.Inject(rate: 1500, interval: TimeSpan.FromSeconds(1),
            during: TimeSpan.FromSeconds(durationSeconds))
    );

var ttlScenario = Scenario.Create("set_with_ttl", async ctx =>
    {
        var client = ttlPool.Rent();
        try
        {
            string key = $"ttl:{ctx.InvocationNumber}";
            var response = client.Set(key, "expiring", exSeconds: 60);
            ttlPool.Return(client);
            return response == "+OK" ? Response.Ok() : Response.Fail();
        }
        catch (Exception ex)
        {
            ttlPool.Return(client, discard: true);
            return Response.Fail(message: ex.Message);
        }
    })
    .WithWarmUpDuration(TimeSpan.FromSeconds(warmupSeconds))
    .WithLoadSimulations(
        Simulation.Inject(rate: 500, interval: TimeSpan.FromSeconds(1),
            during: TimeSpan.FromSeconds(durationSeconds))
    );

try
{
    NBomberRunner
        .RegisterScenarios(pingScenario, setScenario, getScenario, mixedScenario, ttlScenario)
        .WithReportFolder("load-test-results")
        .WithReportFormats(NBomber.Contracts.Stats.ReportFormat.Html, NBomber.Contracts.Stats.ReportFormat.Md)
        .Run();
}
finally
{
    // Закрывем все пулы соединений
    pingPool.Dispose();
    setPool.Dispose();
    getPool.Dispose();
    mixedPool.Dispose();
    ttlPool.Dispose();
}

Console.WriteLine();
Console.WriteLine("Результаты сохранены в: load-test-results/");
