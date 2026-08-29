namespace Roadbed.Net.Mcp.Fetching;

using System.Threading;
using System.Threading.Tasks;
using global::Roadbed.Net.Mcp.Models;

/// <summary>
/// The guarded retrieval contract the tool layer depends on. Everything behind it - the
/// validation zones, the spacing state, the usage cap - is internal to this assembly.
/// </summary>
public interface IFetchService
{
    /// <summary>
    /// Retrieves one URL, running the full validation pipeline and following redirects by hand.
    /// </summary>
    /// <param name="url">The absolute HTTPS URL to retrieve.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result. Callers read <c>outcome</c>, never <c>status</c> alone.</returns>
    Task<GetResult> GetAsync(string? url, CancellationToken cancellationToken = default);
}
