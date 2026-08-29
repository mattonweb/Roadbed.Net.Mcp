namespace Roadbed.Net.Mcp.Models;

using System.Text.Json.Serialization;

/// <summary>
/// The result category of a <c>Get</c> call. The caller must read this rather than
/// inferring a category from <see cref="GetResult.Status"/> alone: a challenge page and
/// an empty-but-legitimate page both arrive as HTTP 200.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FetchOutcome>))]
public enum FetchOutcome
{
    /// <summary>2xx, bytes returned.</summary>
    [JsonStringEnumMemberName("ok")]
    Ok,

    /// <summary>Failed validation. Never contacted. <see cref="GetResult.RefusalReason"/> names the rule.</summary>
    [JsonStringEnumMemberName("refused")]
    Refused,

    /// <summary>Contacted, and the response is a bot wall or challenge, not content.</summary>
    [JsonStringEnumMemberName("blocked")]
    Blocked,

    /// <summary>404 or 410.</summary>
    [JsonStringEnumMemberName("notFound")]
    NotFound,

    /// <summary>Transport failure, timeout, or an unsuccessful status that is not 404/410.</summary>
    [JsonStringEnumMemberName("error")]
    Error,

    /// <summary>Usage cap reached, or the host's circuit is open. Nothing was sent.</summary>
    [JsonStringEnumMemberName("throttled")]
    Throttled,
}
