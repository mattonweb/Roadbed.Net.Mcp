namespace Roadbed.Net.Mcp.Validation;

/// <summary>
/// Stable names for every rule that can refuse a URL. These are part of the tool's contract:
/// they are returned verbatim as <c>refusalReason</c> and asserted by name in the tests, so
/// renaming one is a breaking change to callers.
/// </summary>
internal static class RefusalReasons
{
    #region Zone 1 - String Gate

    public const string UrlEmpty = "url_empty";
    public const string UrlTooLong = "url_too_long";
    public const string UrlHasDisallowedCharacters = "url_has_disallowed_characters";
    public const string SchemeNotHttps = "scheme_not_https";
    public const string AuthorityEmpty = "authority_empty";
    public const string AuthorityHasUserInfo = "authority_has_userinfo";
    public const string AuthorityHasPort = "authority_has_port";
    public const string AuthorityHasEscapeOrBackslash = "authority_has_escape_or_backslash";
    public const string AuthorityIsIpLiteral = "authority_is_ip_literal";
    public const string AuthorityHasEmbeddedIpLabels = "authority_has_embedded_ip_labels";
    public const string AuthorityHasNoDot = "authority_has_no_dot";
    public const string AuthorityTldNotAlphabetic = "authority_tld_not_alphabetic";
    public const string AuthorityIsReservedSuffix = "authority_is_reserved_suffix";

    #endregion

    #region Zone 2 - Parse Gate

    public const string UrlUnparsable = "url_unparsable";
    public const string ParsedSchemeNotHttps = "parsed_scheme_not_https";
    public const string ParsedHostNotDns = "parsed_host_not_dns";
    public const string ParsedUserInfoPresent = "parsed_userinfo_present";
    public const string ParsedPortNot443 = "parsed_port_not_443";

    #endregion

    #region Zone 3 - Network Gate

    public const string HostDidNotResolve = "host_did_not_resolve";
    public const string HostResolvesToNonPublicAddress = "host_resolves_to_non_public_address";

    #endregion

    #region Zone 4 - Redirects

    public const string RedirectLimitExceeded = "redirect_limit_exceeded";
    public const string RedirectWithoutLocation = "redirect_without_location";
    public const string RedirectLocationUnresolvable = "redirect_location_unresolvable";

    #endregion
}
