namespace Roadbed.Net.Mcp;

using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using global::Roadbed.Net.Mcp.Configuration;
using global::Roadbed.Net.Mcp.Fetching;
using global::Roadbed.Net.Mcp.Tools;
using global::Roadbed.Net.Mcp.Validation;

// A guarded HTTP fetch primitive over MCP. stdio transport: stdout is the protocol channel,
// so ALL diagnostics are routed to stderr.
internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        FetchConfig config;
        try
        {
            config = ConfigLoader.Load();
        }
        catch (InvalidOperationException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }

        var builder = Host.CreateApplicationBuilder(args);

        // Route every log to stderr; writing to stdout would corrupt the MCP stream.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IAddressResolver, SystemAddressResolver>();
        builder.Services.AddSingleton<DestinationGuard>();
        builder.Services.AddSingleton<UsageCounter>();
        builder.Services.AddSingleton<HostSpacing>();
        builder.Services.AddSingleton(BuildHttpClient(config));
        builder.Services.AddSingleton<IFetchService, FetchService>();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools([typeof(GetTools)]);

        await builder.Build().RunAsync();
        return 0;
    }

    private static HttpClient BuildHttpClient(FetchConfig config)
    {
        var handler = new SocketsHttpHandler
        {
            // Automatic following OFF. Redirects are followed by hand in FetchService so
            // every Location re-enters zone 1 and then zone 3; letting the handler chase
            // them would take the destination check out of the loop entirely.
            AllowAutoRedirect = false,

            // No cookie jar and no credentials. A destination that needs a login is out of
            // reach by design, not by accident.
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };

        // Zone 3 applied at the moment of connection, not only before it. The pre-flight
        // check resolves the host and rejects private answers; this closes the gap between
        // that resolution and this connect, where a record with a short TTL could change
        // underneath us. Same policy, enforced where the socket actually opens.
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

            foreach (var address in addresses)
            {
                if (!AddressPolicy.IsPublic(address))
                {
                    throw new HttpRequestException(
                        $"Refused to connect: '{host}' resolves to a non-public address.");
                }
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };

        // Timeouts are managed per request in FetchService, which covers the body read too.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
