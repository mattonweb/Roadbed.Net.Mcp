namespace Roadbed.Net.Mcp.Tests;

using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Roadbed.Net.Mcp.Validation;

/// <summary>
/// Zone 3 as a whole, behind the resolver seam: what the guard does with the answers it gets.
/// </summary>
[TestClass]
public sealed class DestinationGuardTests
{
    #region Public Methods

    [TestMethod]
    public async Task PublicAnswer_Passes()
    {
        var guard = new DestinationGuard(new StubAddressResolver().Add("careers.example.com", "93.184.216.34"));

        Assert.IsNull(await guard.InspectAsync("careers.example.com"));
    }

    [TestMethod]
    public async Task InwardPointingHostname_IsRefused()
    {
        // The case the string gate cannot see. Nothing about "careers.example.com" hints at
        // where it points, and this is the only place that finds out.
        var guard = new DestinationGuard(new StubAddressResolver().Add("careers.example.com", "10.0.0.1"));

        Assert.AreEqual(
            RefusalReasons.HostResolvesToNonPublicAddress,
            await guard.InspectAsync("careers.example.com"));
    }

    [TestMethod]
    public async Task EveryAddressMustBePublic_NotJustTheFirst()
    {
        // A host that answers with one public and one private address is refused: which one
        // gets connected to is not ours to choose.
        var guard = new DestinationGuard(
            new StubAddressResolver().Add("split.example.com", "93.184.216.34", "192.168.1.10"));

        Assert.AreEqual(
            RefusalReasons.HostResolvesToNonPublicAddress,
            await guard.InspectAsync("split.example.com"));
    }

    [TestMethod]
    public async Task HostThatDoesNotResolve_IsRefused()
    {
        var resolver = new StubAddressResolver { Default = [] };
        var guard = new DestinationGuard(resolver);

        Assert.AreEqual(RefusalReasons.HostDidNotResolve, await guard.InspectAsync("nowhere.example.com"));
    }

    [TestMethod]
    public async Task IPv6OnlyPrivateAnswer_IsRefused()
    {
        var guard = new DestinationGuard(new StubAddressResolver().Add("v6.example.com", "fd00::1"));

        Assert.AreEqual(
            RefusalReasons.HostResolvesToNonPublicAddress,
            await guard.InspectAsync("v6.example.com"));
    }

    #endregion
}
