namespace Roadbed.Net.Mcp.Tests;

using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Roadbed.Net.Mcp.Validation;

/// <summary>
/// Zone 3's range arithmetic, tested directly. No DNS, no sockets - just the question the
/// only security boundary in this server has to answer correctly every time.
/// </summary>
[TestClass]
public sealed class AddressPolicyTests
{
    #region IPv4 rejected ranges

    [TestMethod]
    [DataRow("0.0.0.0", "0/8")]
    [DataRow("0.255.255.255", "0/8")]
    [DataRow("10.0.0.0", "10/8")]
    [DataRow("10.0.0.1", "10/8")]
    [DataRow("10.255.255.255", "10/8")]
    [DataRow("100.64.0.0", "100.64/10")]
    [DataRow("100.100.1.1", "100.64/10")]
    [DataRow("100.127.255.255", "100.64/10")]
    [DataRow("127.0.0.1", "127/8")]
    [DataRow("127.255.255.254", "127/8")]
    [DataRow("169.254.0.1", "169.254/16")]
    [DataRow("169.254.169.254", "169.254/16")]
    [DataRow("172.16.0.1", "172.16/12")]
    [DataRow("172.24.13.9", "172.16/12")]
    [DataRow("172.31.255.255", "172.16/12")]
    [DataRow("192.0.0.1", "192.0.0/24")]
    [DataRow("192.0.0.255", "192.0.0/24")]
    [DataRow("192.168.0.1", "192.168/16")]
    [DataRow("192.168.255.255", "192.168/16")]
    [DataRow("198.18.0.1", "198.18/15")]
    [DataRow("198.19.255.255", "198.18/15")]
    [DataRow("224.0.0.1", "224/4")]
    [DataRow("239.255.255.255", "224/4")]
    [DataRow("240.0.0.1", "240/4")]
    [DataRow("255.255.255.255", "240/4")]
    public void IPv4_RejectedRanges(string address, string range)
    {
        Assert.IsFalse(
            AddressPolicy.IsPublic(IPAddress.Parse(address)),
            $"{address} is in {range} and must not be treated as public.");
    }

    [TestMethod]

    // The addresses immediately outside each rejected range, so a boundary that drifts by
    // one is caught in the direction that would over-block as well as the one that would
    // under-block.
    [DataRow("1.0.0.1")]
    [DataRow("9.255.255.255")]
    [DataRow("11.0.0.1")]
    [DataRow("100.63.255.255")]
    [DataRow("100.128.0.1")]
    [DataRow("126.255.255.255")]
    [DataRow("128.0.0.1")]
    [DataRow("169.253.255.255")]
    [DataRow("169.255.0.1")]
    [DataRow("172.15.255.255")]
    [DataRow("172.32.0.1")]
    [DataRow("192.0.1.1")]
    [DataRow("192.167.255.255")]
    [DataRow("192.169.0.1")]
    [DataRow("198.17.255.255")]
    [DataRow("198.20.0.1")]
    [DataRow("223.255.255.255")]
    [DataRow("93.184.216.34")]
    [DataRow("8.8.8.8")]
    public void IPv4_PublicAddresses(string address)
    {
        Assert.IsTrue(
            AddressPolicy.IsPublic(IPAddress.Parse(address)),
            $"{address} is public and must not be blocked.");
    }

    #endregion

    #region IPv6 rejected ranges

    [TestMethod]
    [DataRow("::1", "loopback")]
    [DataRow("::", "unspecified")]
    [DataRow("fc00::1", "fc00::/7")]
    [DataRow("fd00::1", "fc00::/7")]
    [DataRow("fdff:ffff::1", "fc00::/7")]
    [DataRow("fe80::1", "fe80::/10")]
    [DataRow("febf:ffff::1", "fe80::/10")]
    [DataRow("ff00::1", "ff00::/8")]
    [DataRow("ff02::1", "ff00::/8")]
    public void IPv6_RejectedRanges(string address, string range)
    {
        Assert.IsFalse(
            AddressPolicy.IsPublic(IPAddress.Parse(address)),
            $"{address} is in {range} and must not be treated as public.");
    }

    [TestMethod]
    [DataRow("2606:2800:220:1:248:1893:25c8:1946")]
    [DataRow("2001:4860:4860::8888")]
    public void IPv6_PublicAddresses(string address)
    {
        Assert.IsTrue(AddressPolicy.IsPublic(IPAddress.Parse(address)), $"{address} must not be blocked.");
    }

    [TestMethod]
    [DataRow("::ffff:10.0.0.1")]
    [DataRow("::ffff:127.0.0.1")]
    [DataRow("::ffff:169.254.169.254")]
    [DataRow("::ffff:192.168.1.1")]
    public void IPv4MappedAddresses_AreUnwrappedAndRechecked(string address)
    {
        // A v4 destination in a v6 costume. Without the unwrap it walks straight through the
        // v6 rules, because none of them describe it.
        Assert.IsFalse(AddressPolicy.IsPublic(IPAddress.Parse(address)), $"{address} must be unwrapped.");
    }

    [TestMethod]
    public void IPv4MappedPublicAddress_StaysPublic()
    {
        Assert.IsTrue(AddressPolicy.IsPublic(IPAddress.Parse("::ffff:93.184.216.34")));
    }

    #endregion
}
