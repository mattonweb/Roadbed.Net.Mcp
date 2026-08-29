namespace Roadbed.Net.Mcp.Validation;

using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// The seam between zone 3 and DNS, so the range arithmetic can be tested without a network.
/// </summary>
internal interface IAddressResolver
{
    /// <summary>
    /// Resolves a host to every address it currently answers with.
    /// </summary>
    /// <param name="host">The DNS host name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Every address for the host; empty when it does not resolve.</returns>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default);
}
