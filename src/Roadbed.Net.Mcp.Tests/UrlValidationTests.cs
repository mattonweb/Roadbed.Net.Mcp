namespace Roadbed.Net.Mcp.Tests;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Roadbed.Net.Mcp.Validation;

/// <summary>
/// Zones 1 and 2, table-driven over hostile input. Every row asserts the SPECIFIC rule that
/// refused, not merely that something was refused: a refusal naming the wrong rule is a
/// refusal that will be debugged wrongly, and a rule that quietly stops firing is a
/// regression these tests exist to catch.
/// </summary>
[TestClass]
public sealed class UrlValidationTests
{
    #region The SDD section 5 measurement table

    [TestMethod]

    // https://evil.com@10.0.0.1/x parses with host 10.0.0.1 and userInfo evil.com: the
    // domain a human reads in a log is the username, not the destination.
    [DataRow("https://evil.com@10.0.0.1/x", RefusalReasons.AuthorityHasUserInfo)]

    // 2130706433 and 0x7f000001 both parse to host 127.0.0.1.
    [DataRow("https://2130706433/x", RefusalReasons.AuthorityIsIpLiteral)]
    [DataRow("https://0x7f000001/x", RefusalReasons.AuthorityIsIpLiteral)]

    // [::1] parses as an IPv6 literal.
    [DataRow("https://[::1]/x", RefusalReasons.AuthorityIsIpLiteral)]

    // intranet parses as Dns and looks perfectly ordinary; it has no dot.
    [DataRow("https://intranet/x", RefusalReasons.AuthorityHasNoDot)]

    // 10.0.0.1.nip.io parses as Dns too - HostNameType cannot see that it points inward.
    [DataRow("https://10.0.0.1.nip.io/x", RefusalReasons.AuthorityHasEmbeddedIpLabels)]

    // .NET rejects this one outright; zone 1 never gets that far, because the '@' rule
    // fires first and names the reason a reader needs.
    [DataRow("https://example.com\\@10.0.0.1/x", RefusalReasons.AuthorityHasUserInfo)]
    public void MeasurementTable_RefusesWithTheNamedRule(string url, string expected)
    {
        AssertRefused(url, expected);
    }

    #endregion

    #region Zone 1, rule by rule

    [TestMethod]
    public void Rule1_TooLong()
    {
        AssertRefused("https://example.com/" + new string('a', 2100), RefusalReasons.UrlTooLong);
    }

    [TestMethod]
    [DataRow("https://exa mple.com/")]
    [DataRow("https://example.com/a\rb")]
    [DataRow("https://example.com/a\nb")]
    [DataRow("https://example.com/a\tb")]
    [DataRow("https://example.com/a\0b")]
    [DataRow("https://exämple.com/")]
    [DataRow("https://еxample.com/")]
    public void Rule2_DisallowedCharacters(string url)
    {
        AssertRefused(url, RefusalReasons.UrlHasDisallowedCharacters);
    }

    [TestMethod]
    [DataRow("http://example.com/")]
    [DataRow("file:///etc/passwd")]
    [DataRow("ftp://example.com/")]
    [DataRow("data:text/html,<h1>x</h1>")]
    [DataRow("javascript:alert(1)")]
    [DataRow("//example.com/")]
    [DataRow("gopher://example.com/")]
    public void Rule3_SchemeMustBeHttps(string url)
    {
        AssertRefused(url, RefusalReasons.SchemeNotHttps);
    }

    [TestMethod]
    public void Rule4_EmptyAuthority()
    {
        AssertRefused("https:///path", RefusalReasons.AuthorityEmpty);
    }

    [TestMethod]
    [DataRow("https://user@example.com/")]
    [DataRow("https://user:secret@example.com/")]
    [DataRow("https://example.com@127.0.0.1/")]
    public void Rule5_UserInfo(string url)
    {
        AssertRefused(url, RefusalReasons.AuthorityHasUserInfo);
    }

    [TestMethod]
    [DataRow("https://example.com:8443/")]
    [DataRow("https://example.com:443/")]
    public void Rule6_ExplicitPort(string url)
    {
        AssertRefused(url, RefusalReasons.AuthorityHasPort);
    }

    [TestMethod]
    [DataRow("https://exa%2emple.com/")]
    [DataRow("https://example.com\\evil.test/")]
    public void Rule7_EscapeOrBackslash(string url)
    {
        AssertRefused(url, RefusalReasons.AuthorityHasEscapeOrBackslash);
    }

    [TestMethod]
    [DataRow("https://10.0.0.1/")]
    [DataRow("https://127.0.0.1/")]
    [DataRow("https://169.254.169.254/latest/meta-data/")]
    [DataRow("https://0x7f.1/")]
    [DataRow("https://[fd00::1]/")]
    public void Rule8_IpLiteral(string url)
    {
        AssertRefused(url, RefusalReasons.AuthorityIsIpLiteral);
    }

    [TestMethod]
    [DataRow("https://10.0.0.1.nip.io/")]
    [DataRow("https://192.168.1.1.sslip.io/")]
    [DataRow("https://169.254.169.254.example.com/")]
    public void Rule9_EmbeddedIpLabels(string url)
    {
        AssertRefused(url, RefusalReasons.AuthorityHasEmbeddedIpLabels);
    }

    [TestMethod]
    [DataRow("https://localhost/")]
    [DataRow("https://intranet/")]
    [DataRow("https://wiki/")]
    public void Rule10_NoDot(string url)
    {
        AssertRefused(url, RefusalReasons.AuthorityHasNoDot);
    }

    [TestMethod]
    [DataRow("https://example.c/")]
    [DataRow("https://example.123/")]
    [DataRow("https://example.co2/")]
    [DataRow("https://example.com./")]
    public void Rule11_FinalLabelShape(string url)
    {
        AssertRefused(url, RefusalReasons.AuthorityTldNotAlphabetic);
    }

    [TestMethod]
    [DataRow("https://printer.local/")]
    [DataRow("https://vault.internal/")]
    [DataRow("https://nas.home.arpa/")]
    [DataRow("https://app.localhost/")]
    [DataRow("https://APP.LOCAL/")]
    public void Rule12_ReservedSuffixes(string url)
    {
        AssertRefused(url, RefusalReasons.AuthorityIsReservedSuffix);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow((string?)null)]
    public void EmptyInput(string? url)
    {
        AssertRefused(url, RefusalReasons.UrlEmpty);
    }

    #endregion

    #region What must still get through

    [TestMethod]

    // No TLD allowlist. It was considered and rejected: it defends nothing, because a .com
    // resolves into private space exactly as easily as anything else, while silently
    // blocking real destinations. These are the shapes an allowlist would have broken.
    [DataRow("https://example.com/")]
    [DataRow("https://boards.example.co/openings")]
    [DataRow("https://apply.example.link/x")]
    [DataRow("https://careers.example.org/")]
    [DataRow("https://jobs.example.jobs/")]
    [DataRow("https://example.museum/")]
    [DataRow("https://sub.domain.example.co.uk/a/b?c=d&e=f#g")]
    [DataRow("https://xn--80ak6aa92e.com/")]
    [DataRow("https://example.com")]
    [DataRow("HTTPS://EXAMPLE.COM/Path")]
    [DataRow("https://a1.b2.example.net/")]
    [DataRow("https://1.2.3.example.com/")]
    public void LegitimateUrlsPass(string url)
    {
        var passed = UrlValidator.TryValidate(url, out var uri, out var reason);

        Assert.IsTrue(passed, $"'{url}' was refused as '{reason}'.");
        Assert.IsNotNull(uri);
        Assert.AreEqual(443, uri.Port);
    }

    [TestMethod]
    public void ThreeNumericLabelsAreNotEnoughToTripRule9()
    {
        // The rule is four consecutive numeric labels, which is what an embedded IPv4
        // address looks like. Three is an ordinary, if odd, hostname.
        Assert.IsTrue(UrlValidator.TryValidate("https://1.2.3.example.com/", out _, out _));
        Assert.IsFalse(UrlValidator.TryValidate("https://1.2.3.4.example.com/", out _, out var reason));
        Assert.AreEqual(RefusalReasons.AuthorityHasEmbeddedIpLabels, reason);
    }

    [TestMethod]
    public void QueryStringDoesNotSmuggleAnIpPastTheAuthorityRules()
    {
        // Reading the authority only as far as the first '/' would leave "10.0.0.1?x" as the
        // authority text, which is not all digits and dots and would slip past rule 8.
        AssertRefused("https://10.0.0.1?x=1", RefusalReasons.AuthorityIsIpLiteral);
        AssertRefused("https://10.0.0.1#f", RefusalReasons.AuthorityIsIpLiteral);
    }

    #endregion

    #region Private Methods

    private static void AssertRefused(string? url, string expectedReason)
    {
        var passed = UrlValidator.TryValidate(url, out var uri, out var reason);

        Assert.IsFalse(passed, $"'{url}' was expected to be refused as '{expectedReason}'.");
        Assert.IsNull(uri);
        Assert.AreEqual(expectedReason, reason, $"'{url}' tripped the wrong rule.");
    }

    #endregion
}
