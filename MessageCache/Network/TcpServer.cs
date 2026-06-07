using System.Net;
using System.Net.Sockets;
using MessageCache.Processing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MessageCache.Network;

public sealed class ServerOptions
{
    public int Port { get; set; } = 6379;
    public string Password { get; set; } = string.Empty;
}

/// <summary>
///     Сервер принимает клиентов и создаёт ClientConnection под каждого
/// </summary>
public sealed class TcpServer(
    CommandProcessor processor,
    SubscriptionManager subscriptionManager,
    IOptions<ServerOptions> options,
    ILogger<TcpServer> logger)
    : BackgroundService
{
    private readonly ServerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, _options.Port);
        listener.Start(backlog: 128);

        logger.LogInformation("MessageCache server listening on port {Port}", _options.Port);
        if (!string.IsNullOrEmpty(_options.Password))
            logger.LogInformation("Password authentication is enabled");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await listener.AcceptSocketAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                socket.NoDelay = true;
                socket.ReceiveTimeout = 0;

                _ = HandleClientAsync(socket, ct);
            }
        }
        finally
        {
            listener.Stop();
            logger.LogInformation("Server stopped");
        }
    }

    private async Task HandleClientAsync(Socket socket, CancellationToken serverToken)
    {
        bool requireAuth = !string.IsNullOrEmpty(_options.Password);

        await using var connection = new ClientConnection(
            socket, processor, subscriptionManager, requireAuth, logger);

        await connection.RunAsync(serverToken);
    }
}
