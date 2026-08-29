namespace Roadbed.Net.Mcp.Tests;

using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Roadbed.Net.Mcp.Configuration;
using Roadbed.Net.Mcp.Fetching;
using Roadbed.Net.Mcp.Validation;

/// <summary>
/// A resolver that answers from a table instead of DNS, so zone 3 is exercised without a
/// network and a host can be made to point wherever a test needs it to.
/// </summary>
internal sealed class StubAddressResolver : IAddressResolver
{
    private readonly Dictionary<string, IPAddress[]> _answers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the hosts this resolver was asked about, in order.</summary>
    public List<string> Queried { get; } = [];

    /// <summary>Gets or sets the answer given for a host with no explicit entry.</summary>
    public IPAddress[] Default { get; set; } = [IPAddress.Parse("93.184.216.34")];

    public StubAddressResolver Add(string host, params string[] addresses)
    {
        this._answers[host] = Array.ConvertAll(addresses, IPAddress.Parse);
        return this;
    }

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        this.Queried.Add(host);

        var answer = this._answers.TryGetValue(host, out var addresses) ? addresses : this.Default;
        return Task.FromResult<IReadOnlyList<IPAddress>>(answer);
    }
}

/// <summary>
/// A handler that answers from a script keyed by absolute URL, so redirect chains can be
/// built exactly and nothing leaves the machine.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the absolute URLs this handler was asked for, in order.</summary>
    public List<string> Requested { get; } = [];

    /// <summary>Gets or sets an exception to throw instead of answering.</summary>
    public Exception? Throw { get; set; }

    public StubHttpMessageHandler Route(string url, Func<HttpResponseMessage> respond)
    {
        this._routes[url] = respond;
        return this;
    }

    public StubHttpMessageHandler RouteRedirect(string url, HttpStatusCode status, string location)
    {
        return this.Route(url, () =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.TryAddWithoutValidation("Location", location);
            return response;
        });
    }

    public StubHttpMessageHandler RouteBody(
        string url,
        string body,
        HttpStatusCode status = HttpStatusCode.OK,
        string contentType = "text/html")
    {
        return this.Route(url, () => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        });
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        this.Requested.Add(url);

        if (this.Throw is not null)
        {
            throw this.Throw;
        }

        if (!this._routes.TryGetValue(url, out var respond))
        {
            throw new HttpRequestException($"The test defined no route for '{url}'.");
        }

        return Task.FromResult(respond());
    }
}

/// <summary>
/// Assembles a <see cref="FetchService"/> over the test doubles.
/// </summary>
internal static class TestFactory
{
    public static FetchConfig Config()
    {
        // No spacing delay: politeness is tested on its own, and every other test would
        // otherwise pay a second per hop.
        return new FetchConfig { MinHostIntervalMilliseconds = 0 };
    }

    public static FetchService Fetcher(
        StubHttpMessageHandler handler,
        StubAddressResolver resolver,
        FetchConfig? config = null)
    {
        config ??= Config();

        return new FetchService(
            new HttpClient(handler),
            config,
            new DestinationGuard(resolver),
            new UsageCounter(config),
            new HostSpacing(config, TimeProvider.System),
            NullLogger<FetchService>.Instance);
    }

    public static string Fixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        return File.ReadAllText(path);
    }
}
