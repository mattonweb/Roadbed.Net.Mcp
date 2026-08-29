namespace Roadbed.Net.Mcp.Tests;

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Pins the measurements SDD 5 rests on.
/// </summary>
/// <remarks>
/// The design's argument for rules 5, 8 and 9 is not "these strings look wrong", it is "this
/// is what .NET's parser actually does with them". Those readings were taken on 2026-08-29
/// against one runtime. If a future runtime changes them, the reasoning behind the string
/// gate changes with it, and this is where that shows up - as a failing test, rather than as
/// a rule everyone assumes still earns its place.
/// </remarks>
[TestClass]
public sealed class UriMeasurementTests
{
    #region Public Methods

    [TestMethod]
    public void CredentialedUrl_HostIsTheAddress_NotTheDomain()
    {
        var uri = new Uri("https://evil.com@10.0.0.1/x");

        Assert.AreEqual("10.0.0.1", uri.Host);
        Assert.AreEqual("evil.com", uri.UserInfo);
        Assert.AreEqual(UriHostNameType.IPv4, uri.HostNameType);
    }

    [TestMethod]
    [DataRow("https://2130706433/x")]
    [DataRow("https://0x7f000001/x")]
    public void NumericAndHexHosts_ParseToLoopback(string url)
    {
        var uri = new Uri(url);

        Assert.AreEqual("127.0.0.1", uri.Host);
        Assert.AreEqual(UriHostNameType.IPv4, uri.HostNameType);
    }

    [TestMethod]
    public void BracketedHost_ParsesAsIPv6()
    {
        var uri = new Uri("https://[::1]/x");

        Assert.AreEqual(UriHostNameType.IPv6, uri.HostNameType);
    }

    [TestMethod]
    public void DottedQuadInAHostname_ParsesAsAnOrdinaryDnsName()
    {
        // This is the row that matters most. 10.0.0.1.nip.io is Dns, so HostNameType says
        // nothing useful about it, and any domain owner can achieve the same thing with no
        // numeric pattern at all. Rule 9 catches the visible family; zone 3 catches the rest.
        var uri = new Uri("https://10.0.0.1.nip.io/x");

        Assert.AreEqual("10.0.0.1.nip.io", uri.Host);
        Assert.AreEqual(UriHostNameType.Dns, uri.HostNameType);
    }

    [TestMethod]
    public void PlainName_ParsesAsDns()
    {
        var uri = new Uri("https://intranet/x");

        Assert.AreEqual("intranet", uri.Host);
        Assert.AreEqual(UriHostNameType.Dns, uri.HostNameType);
    }

    [TestMethod]
    public void BackslashCredentialForm_IsRejectedByTheParser()
    {
        Assert.IsFalse(Uri.TryCreate("https://example.com\\@10.0.0.1/x", UriKind.Absolute, out _));
    }

    #endregion
}
