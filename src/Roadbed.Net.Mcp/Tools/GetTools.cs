namespace Roadbed.Net.Mcp.Tools;

using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using global::Roadbed.Net.Mcp.Fetching;
using global::Roadbed.Net.Mcp.Lib;

/// <summary>
/// The tool surface: one guarded HTTPS retrieval.
/// </summary>
[McpServerToolType]
public static class GetTools
{
    #region Public Methods

    /// <summary>
    /// Retrieves one HTTPS URL and returns the raw response.
    /// </summary>
    /// <param name="fetchService">The injected fetch service.</param>
    /// <param name="url">The absolute HTTPS URL to retrieve.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The compact JSON result.</returns>
    [McpServerTool(Name = "Get")]
    [Description("Retrieve one HTTPS URL and return the RAW response body - never a rendered DOM, so "
        + "content assembled by JavaScript will not be present. GET only: no POST, no authentication, "
        + "no cookies, no caching, and no link-following. The destination is validated before anything "
        + "is contacted and again at every redirect. Read 'outcome', not 'status': 'blocked' means a bot "
        + "wall answered, which is not the same as a page with nothing on it. Read 'finalUrl', not the "
        + "URL you asked for. On a refusal, 'refusalStage' says where it happened - 'redirect' means a hop "
        + "the server sent was declined, and 'refusedUrl' names that hop, which is NOT the URL you asked "
        + "for. 'redirectCount' counts hops FOLLOWED, so a refusal at the first hop reports 0: that does "
        + "not mean no redirect happened. Calls are capped per session.")]
    public static async Task<string> Get(
        IFetchService fetchService,
        [Description("Absolute https:// URL. Must be a DNS host name on port 443 - no IP literals, no "
            + "credentials, no explicit port.")]
        string url,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetchService);

        var result = await fetchService.GetAsync(url, cancellationToken).ConfigureAwait(false);
        return Json.Serialize(result);
    }

    #endregion
}
