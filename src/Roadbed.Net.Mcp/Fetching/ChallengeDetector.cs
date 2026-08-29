namespace Roadbed.Net.Mcp.Fetching;

using System;

/// <summary>
/// Decides whether a response is a bot wall rather than content.
/// </summary>
/// <remarks>
/// A challenge and an empty-but-legitimate page both arrive as 200 with valid markup. This
/// is what keeps them apart, and it is why <c>blocked</c> exists as an outcome distinct from
/// <c>ok</c>: an empty page is a fact about the destination, a challenge is a fact about us.
/// </remarks>
internal static class ChallengeDetector
{
    #region Internal Methods

    /// <summary>
    /// Determines whether a response body is a challenge page.
    /// </summary>
    /// <param name="isTextual">Whether the body was decoded as text.</param>
    /// <param name="body">The decoded body, or the base64 payload when binary.</param>
    /// <returns><see langword="true"/> when a challenge marker is present.</returns>
    public static bool IsChallenge(bool isTextual, string? body)
    {
        // Binary payloads are base64 here, and matching markers against base64 would be
        // matching noise. A challenge page is always markup.
        if (!isTextual || string.IsNullOrEmpty(body))
        {
            return false;
        }

        foreach (var marker in ChallengeMarkers.All)
        {
            if (body.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    #endregion
}
