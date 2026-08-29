namespace Roadbed.Net.Mcp.Fetching;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using global::Roadbed.Net.Mcp.Configuration;
using global::Roadbed.Net.Mcp.Models;
using global::Roadbed.Net.Mcp.Validation;

/// <summary>
/// The one place every outbound HTTP policy decision is made: validate, space, send, follow
/// redirects by hand, cap, classify.
/// </summary>
/// <remarks>
/// This exists so those decisions are made once, in code that can be read, instead of afresh
/// in every throwaway script an agent writes. It is NOT a sandbox: an agent that can still
/// run arbitrary code can still do arbitrary things. It narrows one path and makes that path
/// uniform, observable and bounded.
/// </remarks>
internal sealed class FetchService : BaseClassWithLogging, IFetchService
{
    #region Private Fields

    private readonly HttpClient _httpClient;
    private readonly FetchConfig _config;
    private readonly DestinationGuard _destinationGuard;
    private readonly UsageCounter _usageCounter;
    private readonly HostSpacing _hostSpacing;

    #endregion

    #region Public Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="FetchService"/> class.
    /// </summary>
    /// <param name="httpClient">The client, configured with automatic redirects and cookies off.</param>
    /// <param name="config">The fetch configuration.</param>
    /// <param name="destinationGuard">The zone 3 guard.</param>
    /// <param name="usageCounter">The session usage cap.</param>
    /// <param name="hostSpacing">The per-host spacing and circuit state.</param>
    /// <param name="logger">The logger.</param>
    public FetchService(
        HttpClient httpClient,
        FetchConfig config,
        DestinationGuard destinationGuard,
        UsageCounter usageCounter,
        HostSpacing hostSpacing,
        ILogger<FetchService> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(destinationGuard);
        ArgumentNullException.ThrowIfNull(usageCounter);
        ArgumentNullException.ThrowIfNull(hostSpacing);

        this._httpClient = httpClient;
        this._config = config;
        this._destinationGuard = destinationGuard;
        this._usageCounter = usageCounter;
        this._hostSpacing = hostSpacing;
    }

    #endregion

    #region Public Methods

    /// <inheritdoc />
    public async Task<GetResult> GetAsync(string? url, CancellationToken cancellationToken = default)
    {
        var result = new GetResult { RequestedUrl = url ?? string.Empty };

        // ZONE 1 + ZONE 2 on what the caller supplied.
        if (!UrlValidator.TryValidate(url, out var target, out var refusalReason))
        {
            return Refuse(result, refusalReason!);
        }

        var charged = false;

        while (true)
        {
            result.FinalUrl = target!.AbsoluteUri;

            // ZONE 3 - the security boundary. Every hop passes through here, including the
            // first, because a hostname's shape says nothing about where it points.
            var destinationRefusal = await this._destinationGuard
                .InspectAsync(target.Host, cancellationToken)
                .ConfigureAwait(false);

            if (destinationRefusal is not null)
            {
                this.LogWarning(
                    "Refused {Host} at the network gate: {Reason}",
                    target.Host,
                    destinationRefusal);

                return Refuse(result, destinationRefusal);
            }

            if (this._hostSpacing.IsCircuitOpen(target.Host))
            {
                this.LogInformation("Circuit open for {Host}; nothing sent", target.Host);
                return Throttle(result);
            }

            // One unit per Get call that reaches the network. Redirect hops are free: the
            // caller asked for one fetch, not for the chain the server sent it on.
            if (!charged)
            {
                if (!this._usageCounter.TryConsume())
                {
                    this.LogInformation("Session usage cap reached; nothing sent");
                    return Throttle(result);
                }

                charged = true;
            }

            await this._hostSpacing.WaitTurnAsync(target.Host, cancellationToken).ConfigureAwait(false);

            HttpResponseMessage response;
            try
            {
                response = await this.SendAsync(target, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                this._hostSpacing.RecordFailure(target.Host);
                this.LogWarning("Transport failure for {Url}: {Message}", target.AbsoluteUri, ex.Message);

                result.Outcome = FetchOutcome.Error;
                return result;
            }

            using (response)
            {
                var location = ReadRedirectLocation(response);

                if (location is not null)
                {
                    // ZONE 4 - a redirect target is third-party-controlled input. Trust it
                    // least: it re-enters zone 1 and then zone 3, exactly like the original.
                    if (result.RedirectCount >= this._config.MaxRedirects)
                    {
                        return Refuse(result, RefusalReasons.RedirectLimitExceeded);
                    }

                    if (!TryResolveRedirect(target, location, out var next, out var locationRefusal))
                    {
                        this.LogWarning(
                            "Refused a redirect from {Url} to {Location}: {Reason}",
                            target.AbsoluteUri,
                            location,
                            locationRefusal!);

                        return Refuse(result, locationRefusal!);
                    }

                    this._hostSpacing.RecordSuccess(target.Host);
                    result.RedirectCount++;
                    target = next;
                    continue;
                }

                if (IsRedirectStatus(response.StatusCode))
                {
                    return Refuse(result, RefusalReasons.RedirectWithoutLocation);
                }

                return await this.CompleteAsync(result, target, response, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    #endregion

    #region Private Methods

    private static GetResult Refuse(GetResult result, string refusalReason)
    {
        result.Ok = false;
        result.Outcome = FetchOutcome.Refused;
        result.RefusalReason = refusalReason;
        return result;
    }

    private static GetResult Throttle(GetResult result)
    {
        result.Ok = false;
        result.Outcome = FetchOutcome.Throttled;
        return result;
    }

    private static bool IsRedirectStatus(HttpStatusCode status)
    {
        return status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
    }

    private static string? ReadRedirectLocation(HttpResponseMessage response)
    {
        if (!IsRedirectStatus(response.StatusCode))
        {
            return null;
        }

        var location = response.Headers.Location?.OriginalString;
        return string.IsNullOrWhiteSpace(location) ? null : location;
    }

    private static bool TryResolveRedirect(Uri current, string location, out Uri? next, out string? refusalReason)
    {
        next = null;

        // A Location may legitimately be relative, so it is made absolute against the URL it
        // came from before anything else happens. That is only address arithmetic: the
        // result then re-enters zone 1 and zone 3 from the top, so a relative hop earns no
        // more trust than an absolute one.
        if (!Uri.TryCreate(current, location, out var absolute))
        {
            refusalReason = RefusalReasons.RedirectLocationUnresolvable;
            return false;
        }

        if (!UrlValidator.TryValidate(absolute.AbsoluteUri, out next, out refusalReason))
        {
            return false;
        }

        refusalReason = null;
        return true;
    }

    private static FetchOutcome Classify(int status, BodyReadResult body)
    {
        // A challenge page answers 200 with valid markup and no content. It is checked first
        // and at any status, because "we were refused" outranks "the server said fine".
        if (ChallengeDetector.IsChallenge(body.IsTextual, body.Body))
        {
            return FetchOutcome.Blocked;
        }

        if (status is >= 200 and <= 299)
        {
            return FetchOutcome.Ok;
        }

        if (status is 404 or 410)
        {
            return FetchOutcome.NotFound;
        }

        // Everything else - 5xx as SDD 7 says, and the other unsuccessful statuses, which
        // the table does not name and which are nothing else on it.
        return FetchOutcome.Error;
    }

    private async Task<HttpResponseMessage> SendAsync(Uri target, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(this._config.RequestTimeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.TryAddWithoutValidation("User-Agent", this._config.ResolveUserAgent());

        return await this._httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
    }

    private async Task<GetResult> CompleteAsync(
        GetResult result,
        Uri target,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        result.Status = status;
        result.ContentType = response.Content.Headers.ContentType?.ToString();

        BodyReadResult body;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(this._config.RequestTimeoutSeconds));

            body = await BodyReader
                .ReadAsync(response, this._config.MaxResponseBytes, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            this._hostSpacing.RecordFailure(target.Host);
            this.LogWarning("Body read failed for {Url}: {Message}", target.AbsoluteUri, ex.Message);

            result.Outcome = FetchOutcome.Error;
            return result;
        }

        result.Body = body.Body;
        result.ContentLength = body.ByteCount;
        result.Truncated = body.Truncated;

        if (status >= 500)
        {
            this._hostSpacing.RecordFailure(target.Host);
        }
        else
        {
            this._hostSpacing.RecordSuccess(target.Host);
        }

        result.Outcome = Classify(status, body);
        result.Ok = result.Outcome == FetchOutcome.Ok;

        this.LogDebug(
            "Fetched {Url} status {Status} bytes {Bytes} outcome {Outcome}",
            target.AbsoluteUri,
            status,
            body.ByteCount,
            result.Outcome);

        return result;
    }

    #endregion
}
