namespace Roadbed.Net.Mcp.Validation;

using System;

/// <summary>
/// Zones 1 and 2 of SDD 5: the string gate and the parse gate.
/// </summary>
/// <remarks>
/// <para>
/// These are cheap filters. They kill the obvious early and produce a refusal that names a
/// rule, which is worth having - but they are NOT what keeps a fetch safe, and nothing here
/// should ever be read that way. Any hostname can point anywhere: as a string,
/// <c>careers.example.com</c> resolving to <c>10.0.0.1</c> is indistinguishable from one
/// resolving to a public address. Only zone 3 (<see cref="DestinationGuard"/>) can tell
/// those apart, and only zone 3 is a security boundary.
/// </para>
/// <para>
/// Rules are evaluated in the order written, so the first rule a URL trips is the one named
/// in the refusal.
/// </para>
/// </remarks>
internal static class UrlValidator
{
    #region Private Fields

    private const int MaxUrlLength = 2048;

    private const string HttpsPrefix = "https://";

    // Suffixes reserved for names that are, by definition, not on the public internet.
    private static readonly string[] ReservedSuffixes =
    [
        ".local",
        ".internal",
        ".home.arpa",
        ".localhost",
    ];

    #endregion

    #region Internal Methods

    /// <summary>
    /// Runs zone 1 then zone 2 over a candidate URL string.
    /// </summary>
    /// <param name="urlString">The candidate URL, exactly as supplied.</param>
    /// <param name="uri">The parsed URI when both zones pass; otherwise null.</param>
    /// <param name="refusalReason">The name of the first rule that refused; otherwise null.</param>
    /// <returns><see langword="true"/> when the URL's shape is trustworthy. Its destination is not yet.</returns>
    public static bool TryValidate(string? urlString, out Uri? uri, out string? refusalReason)
    {
        uri = null;

        refusalReason = InspectString(urlString);
        if (refusalReason is not null)
        {
            return false;
        }

        refusalReason = InspectParse(urlString!, out uri);
        return refusalReason is null;
    }

    #endregion

    #region Private Methods

    // ZONE 1 - string gate, before Uri.TryCreate. Cheap. Heuristic.
    private static string? InspectString(string? urlString)
    {
        if (string.IsNullOrEmpty(urlString))
        {
            return RefusalReasons.UrlEmpty;
        }

        // 1  length <= 2048
        if (urlString.Length > MaxUrlLength)
        {
            return RefusalReasons.UrlTooLong;
        }

        // 2  every char in 0x21..0x7E - no space, CR, LF, NUL, tab or any non-ASCII
        foreach (var c in urlString)
        {
            if (c < 0x21 || c > 0x7E)
            {
                return RefusalReasons.UrlHasDisallowedCharacters;
            }
        }

        // 3  starts with "https://" - kills http:// file:// ftp:// data: javascript: //host
        if (!urlString.StartsWith(HttpsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return RefusalReasons.SchemeNotHttps;
        }

        // 4  authority := the text after "https://" up to the first delimiter that ends an
        //    authority. RFC 3986 ends it at '/', '?' or '#'. Reading only as far as '/'
        //    would let "https://10.0.0.1?x" carry its query into the authority text and
        //    slip past rule 8, so all three delimiters are honoured.
        var authority = ExtractAuthority(urlString);

        if (authority.Length == 0)
        {
            return RefusalReasons.AuthorityEmpty;
        }

        // 5  authority contains '@' - https://evil.com@10.0.0.1 really goes to 10.0.0.1,
        //    and anyone scanning a log reads the username as the destination.
        if (authority.Contains('@', StringComparison.Ordinal))
        {
            return RefusalReasons.AuthorityHasUserInfo;
        }

        // 6  authority contains ':' - no explicit ports
        if (authority.Contains(':', StringComparison.Ordinal))
        {
            return RefusalReasons.AuthorityHasPort;
        }

        // 7  authority contains '%' or '\'
        if (authority.Contains('%', StringComparison.Ordinal)
            || authority.Contains('\\', StringComparison.Ordinal))
        {
            return RefusalReasons.AuthorityHasEscapeOrBackslash;
        }

        // 8  authority all [0-9.] OR starts '[' OR starts '0x'
        //    kills 10.0.0.1  2130706433  0x7f000001  [::1]
        if (IsAllDigitsAndDots(authority)
            || authority[0] == '['
            || authority.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return RefusalReasons.AuthorityIsIpLiteral;
        }

        var labels = authority.Split('.');

        // 9  four or more consecutive numeric labels - kills 10.0.0.1.nip.io and the whole
        //    embedded-IP-DNS family, which parses as an ordinary hostname.
        if (HasFourConsecutiveNumericLabels(labels))
        {
            return RefusalReasons.AuthorityHasEmbeddedIpLabels;
        }

        // 10  authority contains at least one '.' - kills intranet, localhost
        if (labels.Length < 2)
        {
            return RefusalReasons.AuthorityHasNoDot;
        }

        // 11  final label alphabetic, length >= 2. A shape check, not a TLD allowlist: an
        //     allowlist gives no defence (a .com resolves inward exactly as easily as
        //     anything else) while silently blocking real destinations. See SDD 5.
        var finalLabel = labels[^1];
        if (finalLabel.Length < 2 || !IsEveryChar(finalLabel, char.IsAsciiLetter))
        {
            return RefusalReasons.AuthorityTldNotAlphabetic;
        }

        // 12  not *.local *.internal *.home.arpa *.localhost
        foreach (var suffix in ReservedSuffixes)
        {
            if (authority.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return RefusalReasons.AuthorityIsReservedSuffix;
            }
        }

        return null;
    }

    // ZONE 2 - parse gate. Re-assert on the PARSED object, because the parser need not
    // agree with the string reading above.
    private static string? InspectParse(string urlString, out Uri? uri)
    {
        uri = null;

        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var parsed))
        {
            return RefusalReasons.UrlUnparsable;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return RefusalReasons.ParsedSchemeNotHttps;
        }

        if (parsed.HostNameType != UriHostNameType.Dns)
        {
            return RefusalReasons.ParsedHostNotDns;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            return RefusalReasons.ParsedUserInfoPresent;
        }

        if (parsed.Port != 443)
        {
            return RefusalReasons.ParsedPortNot443;
        }

        uri = parsed;
        return null;
    }

    private static string ExtractAuthority(string urlString)
    {
        var start = HttpsPrefix.Length;
        var end = urlString.Length;

        for (var i = start; i < urlString.Length; i++)
        {
            var c = urlString[i];
            if (c == '/' || c == '?' || c == '#')
            {
                end = i;
                break;
            }
        }

        return urlString[start..end];
    }

    private static bool IsAllDigitsAndDots(string authority)
    {
        return IsEveryChar(authority, static c => char.IsAsciiDigit(c) || c == '.');
    }

    private static bool HasFourConsecutiveNumericLabels(string[] labels)
    {
        var run = 0;

        foreach (var label in labels)
        {
            if (label.Length > 0 && IsEveryChar(label, char.IsAsciiDigit))
            {
                run++;
                if (run >= 4)
                {
                    return true;
                }
            }
            else
            {
                run = 0;
            }
        }

        return false;
    }

    private static bool IsEveryChar(string value, Func<char, bool> predicate)
    {
        foreach (var c in value)
        {
            if (!predicate(c))
            {
                return false;
            }
        }

        return true;
    }

    #endregion
}
