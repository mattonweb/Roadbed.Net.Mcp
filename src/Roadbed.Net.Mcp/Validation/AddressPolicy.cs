namespace Roadbed.Net.Mcp.Validation;

using System.Net;
using System.Net.Sockets;

/// <summary>
/// The range arithmetic behind zone 3 of SDD 5 - whether one address is on the public
/// internet.
/// </summary>
/// <remarks>
/// This is pure, so it is tested directly rather than through DNS. It is the arithmetic
/// half of the only security boundary in this server - <see cref="DestinationGuard"/> is
/// the half that decides which addresses to ask about.
/// </remarks>
internal static class AddressPolicy
{
    #region Internal Methods

    /// <summary>
    /// Determines whether an address is on the public internet.
    /// </summary>
    /// <param name="address">The address to classify.</param>
    /// <returns><see langword="true"/> when the address is public and may be contacted.</returns>
    /// <remarks>
    /// Fails closed: an address family that is neither IPv4 nor IPv6 is not public.
    /// </remarks>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicV4(address),
            AddressFamily.InterNetworkV6 => IsPublicV6(address),
            _ => false,
        };
    }

    #endregion

    #region Private Methods

    private static bool IsPublicV4(IPAddress address)
    {
        var b = address.GetAddressBytes();

        // 0/8         this network
        // 10/8        private
        // 100.64/10   carrier-grade NAT
        // 127/8       loopback
        // 169.254/16  link-local
        // 172.16/12   private
        // 192.0.0/24  IETF protocol assignments
        // 192.168/16  private
        // 198.18/15   benchmarking
        // 224/4       multicast
        // 240/4       reserved (255.255.255.255 included)
        return !(b[0] == 0
            || b[0] == 10
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || b[0] == 127
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 0 && b[2] == 0)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
            || (b[0] >= 224 && b[0] <= 239)
            || b[0] >= 240);
    }

    private static bool IsPublicV6(IPAddress address)
    {
        // An IPv4-mapped address is an IPv4 destination wearing a v6 costume: unwrap it and
        // re-check the inner v4, or ::ffff:10.0.0.1 walks straight through the v6 rules.
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublicV4(address.MapToIPv4());
        }

        var b = address.GetAddressBytes();

        // ::1        loopback
        // ::         unspecified - not in the SDD's list, but it resolves to the local host
        //            on connect exactly as ::1 does, so the same rule has to cover it.
        // fc00::/7   unique local
        // fe80::/10  link-local
        // ff00::/8   multicast
        return !(address.Equals(IPAddress.IPv6Loopback)
            || address.Equals(IPAddress.IPv6Any)
            || (b[0] & 0xFE) == 0xFC
            || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80)
            || b[0] == 0xFF);
    }

    #endregion
}
