namespace Roadbed.Net.Mcp.Configuration;

using System.Globalization;
using System.Text.Json.Serialization;

/// <summary>
/// Every outbound policy knob, in one place. The host populates it; nothing below this
/// reads configuration for itself.
/// </summary>
/// <remarks>
/// The defaults are the ones ruled in <c>docs/decisions.md</c>. They are deliberately the
/// shipped behaviour, so a consuming repo that supplies no configuration file still gets a
/// self-identifying, capped, spaced client rather than a bare framework agent.
/// </remarks>
public sealed class FetchConfig
{
    #region Public Properties

    /// <summary>
    /// Gets or sets the maximum number of network calls this process will make.
    /// </summary>
    /// <remarks>
    /// PER SESSION, NOT PER HOUR. The counter lives in memory, so under stdio it resets
    /// whenever the server is respawned - it bounds one agent session, and the name says so
    /// rather than implying a wall-clock window the implementation does not have.
    /// </remarks>
    [JsonPropertyName("maxCallsPerSession")]
    public int MaxCallsPerSession { get; set; } = 50;

    /// <summary>Gets or sets the response size cap in bytes. Exceeding it truncates; it is not an error.</summary>
    [JsonPropertyName("maxResponseBytes")]
    public int MaxResponseBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Gets or sets the per-request timeout in seconds, covering headers and body.</summary>
    [JsonPropertyName("requestTimeoutSeconds")]
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Gets or sets the maximum number of redirect hops followed.</summary>
    [JsonPropertyName("maxRedirects")]
    public int MaxRedirects { get; set; } = 5;

    /// <summary>
    /// Gets or sets the minimum interval between requests to the same host, in milliseconds.
    /// </summary>
    /// <remarks>
    /// PER PROCESS, AND UNDER STDIO THAT MEANS PER AGENT. Several agents each spawning their
    /// own server get independent limiters, so the politeness guarantee is per-agent rather
    /// than global. Scheduling them apart is the answer for now; see docs/decisions.md.
    /// </remarks>
    [JsonPropertyName("minHostIntervalMilliseconds")]
    public int MinHostIntervalMilliseconds { get; set; } = 1000;

    /// <summary>Gets or sets the consecutive failures against one host that open its circuit.</summary>
    [JsonPropertyName("circuitFailureThreshold")]
    public int CircuitFailureThreshold { get; set; } = 5;

    /// <summary>Gets or sets how long an open circuit stays open, in seconds.</summary>
    [JsonPropertyName("circuitCooldownSeconds")]
    public int CircuitCooldownSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets a verbatim <c>User-Agent</c>, overriding the self-identifying default.
    /// </summary>
    /// <remarks>
    /// Full browser impersonation is possible here and is a different posture: it forfeits
    /// the contactable half of the default, and does not clear challenge-based protections
    /// anyway - those key on script execution and TLS fingerprints, not the agent string.
    /// It belongs to the owner of each consuming repo, and it is never this repo's default.
    /// </remarks>
    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; set; }

    /// <summary>Gets or sets the product name used to build the default <c>User-Agent</c>.</summary>
    [JsonPropertyName("productName")]
    public string ProductName { get; set; } = "Roadbed.Net.Mcp";

    /// <summary>Gets or sets the product version used to build the default <c>User-Agent</c>.</summary>
    [JsonPropertyName("productVersion")]
    public string ProductVersion { get; set; } = "1.0";

    /// <summary>Gets or sets a URL a publisher can visit to find out who is calling.</summary>
    [JsonPropertyName("contactUrl")]
    public string? ContactUrl { get; set; }

    /// <summary>Gets or sets an address a publisher can write to before deciding to block.</summary>
    [JsonPropertyName("contactEmail")]
    public string? ContactEmail { get; set; }

    #endregion

    #region Public Methods

    /// <summary>
    /// Builds the effective <c>User-Agent</c>: the verbatim override when one is configured,
    /// otherwise the self-identifying default form from SDD 6.
    /// </summary>
    /// <returns>The header value to send.</returns>
    public string ResolveUserAgent()
    {
        if (!string.IsNullOrWhiteSpace(this.UserAgent))
        {
            return this.UserAgent;
        }

        // Mozilla/5.0 (compatible; {ProductName}/{Version}; +{ContactUrl}; {ContactEmail})
        // Browser-compatible enough to clear WAFs that block bare framework agents, while
        // still telling a publisher who we are and how to reach us before they block us.
        // Contact details are optional; an unconfigured repo still identifies its product.
        var parts = new List<string>
        {
            string.Format(
                CultureInfo.InvariantCulture,
                "compatible; {0}/{1}",
                this.ProductName,
                this.ProductVersion),
        };

        if (!string.IsNullOrWhiteSpace(this.ContactUrl))
        {
            parts.Add("+" + this.ContactUrl.Trim());
        }

        if (!string.IsNullOrWhiteSpace(this.ContactEmail))
        {
            parts.Add(this.ContactEmail.Trim());
        }

        return "Mozilla/5.0 (" + string.Join("; ", parts) + ")";
    }

    /// <summary>
    /// Validates the configured values.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when a value is outside a workable range.</exception>
    public void Validate()
    {
        Require(this.MaxCallsPerSession >= 1, "maxCallsPerSession must be at least 1.");
        Require(this.MaxResponseBytes >= 1024, "maxResponseBytes must be at least 1024.");
        Require(this.RequestTimeoutSeconds is >= 1 and <= 600, "requestTimeoutSeconds must be between 1 and 600.");
        Require(this.MaxRedirects is >= 0 and <= 20, "maxRedirects must be between 0 and 20.");
        Require(this.MinHostIntervalMilliseconds >= 0, "minHostIntervalMilliseconds must not be negative.");
        Require(this.CircuitFailureThreshold >= 1, "circuitFailureThreshold must be at least 1.");
        Require(this.CircuitCooldownSeconds >= 0, "circuitCooldownSeconds must not be negative.");
        Require(!string.IsNullOrWhiteSpace(this.ProductName), "productName must not be blank.");
        Require(!string.IsNullOrWhiteSpace(this.ProductVersion), "productVersion must not be blank.");
    }

    #endregion

    #region Private Methods

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    #endregion
}
