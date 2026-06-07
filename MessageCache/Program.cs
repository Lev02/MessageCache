using MessageCache.Core;
using MessageCache.Network;
using MessageCache.Processing;
using MessageCache.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("Server"));

builder.Services.AddSingleton<CacheStorage>();
builder.Services.AddSingleton<SubscriptionManager>();
builder.Services.AddSingleton<CacheMetrics>();

builder.Services.AddSingleton<CommandProcessor>(sp =>
{
    var storage = sp.GetRequiredService<CacheStorage>();
    var subs = sp.GetRequiredService<SubscriptionManager>();
    var metrics = sp.GetRequiredService<CacheMetrics>();
    var options = sp.GetRequiredService<IOptions<ServerOptions>>().Value;
    return new CommandProcessor(storage, subs, metrics, options.Password);
});

builder.Services.AddHostedService<TcpServer>();

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(CacheMetrics.MeterName);
        metrics.AddConsoleExporter(); // prints metrics to console every 10s
    })
    .WithTracing(tracing =>
    {
        tracing.AddSource(CacheMetrics.MeterName);
        tracing.AddConsoleExporter();
    });

builder.Logging.AddConsole();

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
var opts = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;

logger.LogInformation("MessageCache Server 1.0");
logger.LogInformation("In-Memory Key-Value Store (.NET8)");
logger.LogInformation("Port: {Port} | Auth: {Auth}",
    opts.Port, string.IsNullOrEmpty(opts.Password) ? "disabled" : "enabled");
logger.LogInformation("Protocol commands: PING, AUTH, SET, GET, DELETE, EXPIRE, TTL, KEYS, WATCH, UNWATCH, STATS");

await app.RunAsync();
