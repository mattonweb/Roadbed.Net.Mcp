namespace Roadbed.Net.Mcp.Fetching;

/// <summary>
/// The markers that identify a bot wall or challenge page.
/// </summary>
/// <remarks>
/// <para>
/// THESE LIVE HERE, VERSIONED - NOT IN AN AGENT'S PROMPT. A challenge page returns 200 with
/// valid markup and no useful content. A caller that cannot tell it from an empty page will
/// record "nothing here" as a settled fact about a destination that simply refused it: a
/// wrong conclusion that looks finished and never gets revisited. Keeping the list in source
/// means it can be corrected once, reviewed, and dated.
/// </para>
/// <para>
/// Every entry is chosen to be distinctive enough that an ordinary page will not carry it.
/// Weak markers - a bare "captcha", or "recaptcha", which appears on any page with a contact
/// form - are deliberately absent: a false <c>blocked</c> is the same wrong-and-final
/// conclusion in the other direction.
/// </para>
/// </remarks>
internal static class ChallengeMarkers
{
    #region Internal Fields

    /// <summary>The revision of the marker list, bumped whenever an entry changes.</summary>
    public const string Version = "2026-08-29";

    /// <summary>The markers, matched case-insensitively against a textual response body.</summary>
    public static readonly string[] All =
    [

        // Cloudflare
        "cf-browser-verification",
        "cf_chl_opt",
        "__cf_chl_",
        "/cdn-cgi/challenge-platform",
        "checking your browser before accessing",
        "attention required! | cloudflare",
        "sorry, you have been blocked",
        "enable javascript and cookies to continue",
        "verifying you are human",

        // Imperva / Incapsula / Distil
        "_incapsula_resource",
        "incapsula incident id",
        "request unsuccessful. incapsula incident",
        "pardon our interruption",

        // PerimeterX / HUMAN
        "px-captcha",
        "perimeterx",
        "/_px/",

        // DataDome
        "captcha-delivery.com",
        "datadome",

        // AWS WAF
        "awswafintegration",
        "token.awswaf.com",

        // Akamai
        "access denied | akamai",

        // DDoS-Guard, Radware, Google's interstitial
        "ddos-guard",
        "unusual traffic from your computer network",
        "our systems have detected unusual traffic",
    ];

    #endregion
}
