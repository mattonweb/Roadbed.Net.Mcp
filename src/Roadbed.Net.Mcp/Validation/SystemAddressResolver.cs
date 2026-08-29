namespace Roadbed.Net.Mcp.Validation;

using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// The production <see cref="IAddressResolver"/>: the operating system's DNS resolver.
/// </summary>
internal sealed class SystemAddressResolver : IAddressResolver
{
    #region Public Methods

    /// <inheritdoc />
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        try
        {
            return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // A name that does not resolve is a refusal, not an exception the caller handles.
            return Array.Empty<IPAddress>();
        }
    }

    #endregion
}
