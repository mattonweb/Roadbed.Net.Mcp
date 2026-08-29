namespace Roadbed.Net.Mcp.Validation;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Zone 3 of SDD 5 - THE ACTUAL SECURITY BOUNDARY.
/// </summary>
/// <remarks>
/// <para>
/// Resolve the host; every address it answers with must be public. This is the only gate in
/// the server that can tell <c>careers.example.com -&gt; 10.0.0.1</c> from an ordinary
/// destination. Zones 1 and 2 cannot: as a string, that host is indistinguishable from a
/// real one, and any domain owner can point a record at private space with no numeric
/// pattern at all.
/// </para>
/// <para>
/// Every address, not the first: a host that answers with one public and one private
/// address is refused, because which one gets connected to is not ours to choose.
/// </para>
/// </remarks>
internal sealed class DestinationGuard
{
    #region Private Fields

    private readonly IAddressResolver _resolver;

    #endregion

    #region Public Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="DestinationGuard"/> class.
    /// </summary>
    /// <param name="resolver">The address resolver.</param>
    public DestinationGuard(IAddressResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        this._resolver = resolver;
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Resolves a host and checks every address it answers with.
    /// </summary>
    /// <param name="host">The DNS host name from the validated URI.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The refusal reason, or <see langword="null"/> when the destination is safe to contact.</returns>
    public async Task<string?> InspectAsync(string host, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var addresses = await this._resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);

        if (addresses.Count == 0)
        {
            return RefusalReasons.HostDidNotResolve;
        }

        foreach (var address in addresses)
        {
            if (!AddressPolicy.IsPublic(address))
            {
                return RefusalReasons.HostResolvesToNonPublicAddress;
            }
        }

        return null;
    }

    #endregion
}
