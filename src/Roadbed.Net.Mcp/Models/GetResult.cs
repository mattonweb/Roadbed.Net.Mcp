namespace Roadbed.Net.Mcp.Models;

using System.Text.Json.Serialization;

/// <summary>
/// The result of one <c>Get</c> call.
/// </summary>
/// <remarks>
/// <see cref="Body"/> is the raw response body exactly as the server sent it. It is never
/// a rendered DOM, and no code path in this server renders one. A caller reasoning about
/// post-JavaScript markup would be reasoning about content it will never actually receive.
/// </remarks>
public sealed class GetResult
{
    #region Public Properties

    /// <summary>Gets or sets a value indicating whether the call succeeded (<see cref="Outcome"/> is <c>ok</c>).</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    /// <summary>Gets or sets the result category. Never infer this from <see cref="Status"/> alone.</summary>
    [JsonPropertyName("outcome")]
    public FetchOutcome Outcome { get; set; }

    /// <summary>Gets or sets exactly what the caller asked for.</summary>
    [JsonPropertyName("requestedUrl")]
    public string RequestedUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the URL the body actually came from. May differ from the request; callers must read this one.</summary>
    [JsonPropertyName("finalUrl")]
    public string? FinalUrl { get; set; }

    /// <summary>Gets or sets the number of redirects followed.</summary>
    [JsonPropertyName("redirectCount")]
    public int RedirectCount { get; set; }

    /// <summary>Gets or sets the HTTP status of the final response. Null when nothing was sent.</summary>
    [JsonPropertyName("status")]
    public int? Status { get; set; }

    /// <summary>Gets or sets the content type as the server declared it. Not trusted, not enforced.</summary>
    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }

    /// <summary>Gets or sets the number of body bytes read off the wire.</summary>
    [JsonPropertyName("contentLength")]
    public int ContentLength { get; set; }

    /// <summary>
    /// Gets or sets the raw response body. Textual content types are decoded to text;
    /// everything else is base64-encoded with <see cref="ContentType"/> left intact.
    /// </summary>
    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the size cap stopped the read.</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    /// <summary>Gets or sets the name of the rule that refused the request. Present when <see cref="Outcome"/> is <c>refused</c>.</summary>
    [JsonPropertyName("refusalReason")]
    public string? RefusalReason { get; set; }

    /// <summary>
    /// Gets or sets the exact URL the refusing rule was applied to. Present when
    /// <see cref="Outcome"/> is <c>refused</c>.
    /// </summary>
    /// <remarks>
    /// On a redirect refusal this is the hop that was declined, resolved to absolute form,
    /// and NOT <see cref="RequestedUrl"/> or <see cref="FinalUrl"/> - both of those still
    /// name a URL that passed.
    /// </remarks>
    [JsonPropertyName("refusedUrl")]
    public string? RefusedUrl { get; set; }

    /// <summary>
    /// Gets or sets where the refusal happened - <c>request</c> or <c>redirect</c>. Present
    /// when <see cref="Outcome"/> is <c>refused</c>.
    /// </summary>
    /// <remarks>
    /// <c>request</c> only when the refused URL is the one the caller supplied and no hop
    /// has been followed; otherwise <c>redirect</c>. Without this a redirect refusal and a
    /// bad caller URL hand back the same <see cref="RefusalReason"/> and cannot be told
    /// apart.
    /// </remarks>
    [JsonPropertyName("refusalStage")]
    public string? RefusalStage { get; set; }

    #endregion
}
