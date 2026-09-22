namespace Roadbed.Net.Mcp.Tests;

using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Roadbed.Net.Mcp.Configuration;
using Roadbed.Net.Mcp.Models;
using Roadbed.Net.Mcp.Validation;

/// <summary>
/// The whole pipeline end to end, over stubs: redirects, outcomes, caps and the raw body.
/// </summary>
[TestClass]
public sealed class FetchServiceTests
{
    #region Redirects - zone 4

    [TestMethod]
    public async Task RedirectChainEndingAtAPrivateAddress_IsRefused()
    {
        // THE test. The chain starts somewhere perfectly ordinary and ends at a hostname
        // that resolves inward. Nothing about the strings gives it away: only re-entering
        // zone 3 on the redirect target catches it, and this is the path most likely to
        // regress if automatic redirect following is ever switched back on.
        var handler = new StubHttpMessageHandler()
            .RouteRedirect("https://start.example.com/", HttpStatusCode.Found, "https://hop.example.com/next")
            .RouteRedirect("https://hop.example.com/next", HttpStatusCode.Found, "https://inside.example.com/x");

        var resolver = new StubAddressResolver()
            .Add("start.example.com", "93.184.216.34")
            .Add("hop.example.com", "93.184.216.35")
            .Add("inside.example.com", "10.0.0.1");

        var result = await TestFactory.Fetcher(handler, resolver).GetAsync("https://start.example.com/");

        Assert.AreEqual(FetchOutcome.Refused, result.Outcome);
        Assert.AreEqual(RefusalReasons.HostResolvesToNonPublicAddress, result.RefusalReason);
        Assert.AreEqual("https://inside.example.com/x", result.FinalUrl);
        Assert.AreEqual(2, result.RedirectCount);

        // The refused hop was never contacted.
        CollectionAssert.DoesNotContain(handler.Requested, "https://inside.example.com/x");
    }

    [TestMethod]
    public async Task RedirectToPlainHttp_IsRefusedNotDowngraded()
    {
        var handler = new StubHttpMessageHandler()
            .RouteRedirect("https://start.example.com/", HttpStatusCode.MovedPermanently, "http://start.example.com/");

        var result = await TestFactory.Fetcher(handler, new StubAddressResolver())
            .GetAsync("https://start.example.com/");

        Assert.AreEqual(FetchOutcome.Refused, result.Outcome);
        Assert.AreEqual(RefusalReasons.SchemeNotHttps, result.RefusalReason);
    }

    [TestMethod]
    public async Task RedirectToPlainHttp_NamesTheHopItDeclined()
    {
        // The defect this pins: the reason string here is identical to the one a caller's
        // own http:// URL earns, finalUrl is still the clean https URL the caller asked
        // for, and redirectCount is 0 because the hop was never followed. Read together
        // those three said "your valid https URL was refused for no reason". The stage and
        // the refused URL are what make the http:// hop visible.
        var handler = new StubHttpMessageHandler()
            .RouteRedirect("https://start.example.com/", HttpStatusCode.MovedPermanently, "http://start.example.com/");

        var result = await TestFactory.Fetcher(handler, new StubAddressResolver())
            .GetAsync("https://start.example.com/");

        Assert.AreEqual("http://start.example.com/", result.RefusedUrl);
        Assert.AreEqual(RefusalStages.Redirect, result.RefusalStage);

        // Unchanged, and deliberately so - operator seats read these today.
        Assert.AreEqual(RefusalReasons.SchemeNotHttps, result.RefusalReason);
        Assert.AreEqual(0, result.RedirectCount);
        Assert.AreEqual("https://start.example.com/", result.FinalUrl);
    }

    [TestMethod]
    public async Task ACallersOwnBadUrl_IsStagedAsTheRequest()
    {
        // Same refusalReason as the test above, from the opposite cause. The stage is the
        // only field that separates them.
        var result = await TestFactory.Fetcher(new StubHttpMessageHandler(), new StubAddressResolver())
            .GetAsync("http://example.com/");

        Assert.AreEqual(RefusalReasons.SchemeNotHttps, result.RefusalReason);
        Assert.AreEqual(RefusalStages.Request, result.RefusalStage);
        Assert.AreEqual("http://example.com/", result.RefusedUrl);
    }

    [TestMethod]
    public async Task ARefusedRelativeHop_ReportsTheResolvedUrlNotTheRawHeader()
    {
        // The Location header reads "//10.0.0.1/x". Handing that back would tell an
        // operator nothing; the URL the gate actually judged is the absolute form.
        var handler = new StubHttpMessageHandler()
            .RouteRedirect("https://example.com/a", HttpStatusCode.Found, "//10.0.0.1/x");

        var result = await TestFactory.Fetcher(handler, new StubAddressResolver())
            .GetAsync("https://example.com/a");

        Assert.AreEqual(FetchOutcome.Refused, result.Outcome);
        Assert.AreEqual(RefusalReasons.AuthorityIsIpLiteral, result.RefusalReason);
        Assert.AreEqual("https://10.0.0.1/x", result.RefusedUrl);
        Assert.AreEqual(RefusalStages.Redirect, result.RefusalStage);
    }

    [TestMethod]
    public async Task AHopRefusedAfterOneFollowedHop_CountsThatHop()
    {
        // Pins the increment ordering that made the defect invisible: redirectCount counts
        // hops FOLLOWED, so it is 1 here and 0 when the first hop is the refused one.
        var handler = new StubHttpMessageHandler()
            .RouteRedirect("https://start.example.com/", HttpStatusCode.Found, "https://second.example.com/a")
            .RouteRedirect("https://second.example.com/a", HttpStatusCode.Found, "http://second.example.com/b");

        var result = await TestFactory.Fetcher(handler, new StubAddressResolver())
            .GetAsync("https://start.example.com/");

        Assert.AreEqual(FetchOutcome.Refused, result.Outcome);
        Assert.AreEqual(RefusalStages.Redirect, result.RefusalStage);
        Assert.AreEqual("http://second.example.com/b", result.RefusedUrl);
        Assert.AreEqual(1, result.RedirectCount);
    }

    [TestMethod]
    public async Task RedirectToAnIpLiteral_IsRefusedByTheStringGate()
    {
        var handler = new StubHttpMessageHandler()
            .RouteRedirect("https://start.example.com/", HttpStatusCode.Found, "https://169.254.169.254/latest/");

        var result = await TestFactory.Fetcher(handler, new StubAddressResolver())
            .GetAsync("https://start.example.com/");

        Assert.AreEqual(FetchOutcome.Refused, result.Outcome);
        Assert.AreEqual(RefusalReasons.AuthorityIsIpLiteral, result.RefusalReason);
    }

    [TestMethod]
    public async Task FiveHopsAreFollowed_TheSixthIsRefused()
    {
        var handler = new StubHttpMessageHandler();

        for (var i = 0; i < 6; i++)
        {
            handler.RouteRedirect(
                $"https://example.com/{i}",
                HttpStatusCode.Found,
                $"https://example.com/{i + 1}");
        }

        var result = await TestFactory.Fetcher(handler, new StubAddressResolver())
            .GetAsync("https://example.com/0");

        Assert.AreEqual(FetchOutcome.Refused, result.Outcome);
        Assert.AreEqual(RefusalReasons.RedirectLimitExceeded, result.RefusalReason);
        Assert.AreEqual(5, result.RedirectCount);
        Assert.AreEqual(6, handler.Requested.Count);
    }

    [TestMethod]
    public async Task RelativeRedirect_IsResolvedThenRevalidated()
    {
        var handler = new StubHttpMessageHandler()
            .RouteRedirect("https://example.com/a", HttpStatusCode.Found, "/b/c")
            .RouteBody("https://example.com/b/c", "<p>arrived</p>");

        var result = await TestFactory.Fetcher(handler, new StubAddressResolver())
            .GetAsync("https://example.com/a");

        Assert.AreEqual(FetchOutcome.Ok, result.Outcome);
        Assert.AreEqual("https://example.com/b/c", result.FinalUrl);
        Assert.AreEqual(1, result.RedirectCount);
    }

    [TestMethod]
    public async Task ProtocolRelativeRedirect_StillRunsEveryZone()
    {
        // "//inside.example.com/x" resolves to https, so the scheme rule cannot catch it.
        // Zone 3 does.
        var handler = new StubHttpMessageHandler()
            .RouteRedirect("https://example.com/a", HttpStatusCode.Found, "//inside.example.com/x");

        var resolver = new StubAddressResolver().Add("inside.example.com", "172.16.4.5");

        var result = await TestFactory.Fetcher(handler, resolver).GetAsync("https://example.com/a");

        Assert.AreEqual(FetchOutcome.Refused, result.Outcome);
        Assert.AreEqual(RefusalReasons.HostResolvesToNonPublicAddress, result.RefusalReason);
    }

    [TestMethod]
    public async Task RedirectWithoutALocationHeader_IsRefused()
    {
        var handler = new StubHttpMessageHandler()
            .Route("https://example.com/a", () => new HttpResponseMessage(HttpStatusCode.Found));

        var result = await TestFactory.Fetcher(handler, new StubAddressResolver())
            .GetAsync("https://example.com/a");

        Assert.AreEqual(FetchOutcome.Refused, result.Outcome);
        Assert.AreEqual(RefusalReasons.RedirectWithoutLocation, result.RefusalReason);
    }

    #endregion

    #region Blocked versus empty - the pair most likely to be conflated

    [TestMethod]
    public async Task ChallengePageAndEmptyPage_ProduceDifferentOutcomes()
    {
        var handler = new StubHttpMessageHandler()
            .RouteBody("https://walled.example.com/", TestFactory.Fixture("challenge-cloudflare.html"))
            .RouteBody("https://open.example.com/", TestFactory.Fixture("empty-legitimate.html"));

        var fetcher = TestFactory.Fetcher(handler, new StubAddressResolver());

        var walled = await fetcher.GetAsync("https://walled.example.com/");
        var open = await fetcher.GetAsync("https://open.example.com/");

        // Both are HTTP 200 with valid markup and nothing useful on them. A caller that read
        // only `status` would file both as "nothing here" - and would be recording a settled
        // fact about a destination that merely refused it.
        Assert.AreEqual(200, walled.Status);
        Assert.AreEqual(200, open.Status);

        Assert.AreEqual(FetchOutcome.Blocked, walled.Outcome);
        Assert.IsFalse(walled.Ok);

        Assert.AreEqual(FetchOutcome.Ok, open.Outcome);
        Assert.IsTrue(open.Ok);

        Assert.AreNotEqual(walled.Outcome, open.Outcome);
    }

    [TestMethod]
    public async Task AGenuinelyEmptyBody_IsOkNotBlocked()
    {
        var handler = new StubHttpMessageHandler().RouteBody("https://example.com/", string.Empty);

        var result = await TestFactory.Fetcher(handler, new StubAddressResolver())
            .GetAsync("https://example.com/");

        Assert.AreEqual(FetchOutcome.Ok, result.Outcome);
        Assert.AreEqual(0, result.ContentLength);
    }

    [TestMethod]
    public async Task ChallengeMarkersDoNotFireOnOrdinaryProse()
    {
        var handler = new StubHttpMessageHandler().RouteBody(
            "https://example.com/",
            "<p>Our security team uses a captcha and a robots.txt. Please verify your account.</p>");

        var result = await TestFactory.Fetcher(handler, new StubAddressResolver())
            .GetAsync("https://example.com/");

        Assert.AreEqual(FetchOutcome.Ok, result.Outcome);
    }

    #endregion

    #region Outcomes

    [TestMethod]
    [DataRow(HttpStatusCode.NotFound, FetchOutcome.NotFound)]
    [DataRow(HttpStatusCode.Gone, FetchOutcome.NotFound)]
    [DataRow(HttpStatusCode.InternalServerError, FetchOutcome.Error)]
    [DataRow(HttpStatusCode.BadGateway, FetchOutcome.Error)]
    [DataRow(HttpStatusCode.Forbidden, FetchOutcome.Error)]
    [DataRow(HttpStatusCode.TooManyRequests, FetchOutcome.Error)]
    [DataRow(HttpStatusCode.OK, FetchOutcome.Ok)]
    [DataRow(HttpStatusCode.NoContent, FetchOutcome.Ok)]
    public async Task StatusMapsToOutcome(HttpStatusCode status, FetchOutcome expected)
    {
        var handler = new StubHttpMessageHandler().RouteBody("https://example.com/", "<p>x</p>", status);

        var result = await TestFactory.Fetcher(handler, new StubAddressResolver())
            .GetAsync("https://example.com/");

        Assert.AreEqual(expected, result.Outcome);
        Assert.AreEqual((int)status, result.Status);
    }

    [TestMethod]
    public async Task TransportFailure_IsAnError_AndSaysNothingWasRead()
    {
        var handler = new StubHttpMessageHandler { Throw = new HttpRequestException("connection reset") };

        var result = await TestFactory.Fetcher(handler, new StubAddressResolver())
            .GetAsync("https://example.com/");

        Assert.AreEqual(FetchOutcome.Error, result.Outcome);
        Assert.IsNull(result.Status);
        Assert.AreEqual(string.Empty, result.Body);
    }

    [TestMethod]
    public async Task RefusedBeforeAnythingIsSent()
    {
        var handler = new StubHttpMessageHandler();

        var result = await TestFactory.Fetcher(handler, new StubAddressResolver())
            .GetAsync("http://example.com/");

        Assert.AreEqual(FetchOutcome.Refused, result.Outcome);
        Assert.AreEqual(RefusalReasons.SchemeNotHttps, result.RefusalReason);
        Assert.IsNull(result.Status);
        Assert.AreEqual(0, handler.Requested.Count);
        Assert.AreEqual("http://example.com/", result.RequestedUrl);
    }

    #endregion

    #region The body is the raw response

    [TestMethod]
    public async Task BodyIsReturnedByteForByte()
    {
        const string Markup = "<html><body><script>document.write('assembled later')</script></body></html>";

        var handler = new StubHttpMessageHandler().RouteBody("https://example.com/", Markup);

        var result = await TestFactory.Fetcher(handler, new StubAddressResolver())
            .GetAsync("https://example.com/");

        // Exactly what the server sent. Not a rendered DOM, and nothing the script would
        // have produced - that content is not in the response and never will be.
        Assert.AreEqual(Markup, result.Body);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(Markup), result.ContentLength);
        Assert.IsFalse(result.Truncated);
    }

    [TestMethod]
    public async Task OversizeBody_IsTruncatedNotAnError()
    {
        var config = new FetchConfig { MinHostIntervalMilliseconds = 0, MaxResponseBytes = 1024 };
        var handler = new StubHttpMessageHandler().RouteBody("https://example.com/", new string('a', 5000));

        var result = await TestFactory.Fetcher(handler, new StubAddressResolver(), config)
            .GetAsync("https://example.com/");

        Assert.AreEqual(FetchOutcome.Ok, result.Outcome);
        Assert.IsTrue(result.Truncated);
        Assert.AreEqual(1024, result.ContentLength);
        Assert.AreEqual(1024, result.Body.Length);
    }

    [TestMethod]
    public async Task BodyThatExactlyFitsTheCap_IsNotMarkedTruncated()
    {
        var config = new FetchConfig { MinHostIntervalMilliseconds = 0, MaxResponseBytes = 1024 };
        var handler = new StubHttpMessageHandler().RouteBody("https://example.com/", new string('a', 1024));

        var result = await TestFactory.Fetcher(handler, new StubAddressResolver(), config)
            .GetAsync("https://example.com/");

        Assert.IsFalse(result.Truncated);
        Assert.AreEqual(1024, result.ContentLength);
    }

    [TestMethod]
    public async Task BinaryBody_IsBase64WithItsContentTypeIntact()
    {
        var payload = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        var handler = new StubHttpMessageHandler().Route("https://example.com/x.png", () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "image/png");
            return response;
        });

        var result = await TestFactory.Fetcher(handler, new StubAddressResolver())
            .GetAsync("https://example.com/x.png");

        Assert.AreEqual(FetchOutcome.Ok, result.Outcome);
        Assert.AreEqual("image/png", result.ContentType);
        CollectionAssert.AreEqual(payload, Convert.FromBase64String(result.Body));
    }

    #endregion

    #region Caps

    [TestMethod]
    public async Task SessionCap_ThrottlesWithoutSendingAnything()
    {
        var config = new FetchConfig { MinHostIntervalMilliseconds = 0, MaxCallsPerSession = 2 };
        var handler = new StubHttpMessageHandler().RouteBody("https://example.com/", "<p>x</p>");
        var fetcher = TestFactory.Fetcher(handler, new StubAddressResolver(), config);

        Assert.AreEqual(FetchOutcome.Ok, (await fetcher.GetAsync("https://example.com/")).Outcome);
        Assert.AreEqual(FetchOutcome.Ok, (await fetcher.GetAsync("https://example.com/")).Outcome);

        var third = await fetcher.GetAsync("https://example.com/");

        Assert.AreEqual(FetchOutcome.Throttled, third.Outcome);
        Assert.IsNull(third.Status);
        Assert.AreEqual(2, handler.Requested.Count);
    }

    [TestMethod]
    public async Task RefusedCallsDoNotSpendTheBudget()
    {
        var config = new FetchConfig { MinHostIntervalMilliseconds = 0, MaxCallsPerSession = 1 };
        var handler = new StubHttpMessageHandler().RouteBody("https://example.com/", "<p>x</p>");
        var fetcher = TestFactory.Fetcher(handler, new StubAddressResolver(), config);

        await fetcher.GetAsync("http://example.com/");
        await fetcher.GetAsync("https://10.0.0.1/");

        Assert.AreEqual(FetchOutcome.Ok, (await fetcher.GetAsync("https://example.com/")).Outcome);
    }

    [TestMethod]
    public async Task AWholeRedirectChainCostsOneCall()
    {
        var config = new FetchConfig { MinHostIntervalMilliseconds = 0, MaxCallsPerSession = 1 };
        var handler = new StubHttpMessageHandler()
            .RouteRedirect("https://example.com/a", HttpStatusCode.Found, "https://example.com/b")
            .RouteRedirect("https://example.com/b", HttpStatusCode.Found, "https://example.com/c")
            .RouteBody("https://example.com/c", "<p>x</p>");

        var result = await TestFactory.Fetcher(handler, new StubAddressResolver(), config)
            .GetAsync("https://example.com/a");

        // The caller asked for one fetch, not for the chain the server sent it on.
        Assert.AreEqual(FetchOutcome.Ok, result.Outcome);
        Assert.AreEqual(2, result.RedirectCount);
    }

    #endregion
}
