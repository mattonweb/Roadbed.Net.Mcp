namespace Roadbed.Net.Mcp.Validation;

/// <summary>
/// Stable names for where a refusal happened. Like <see cref="RefusalReasons"/> these are
/// part of the tool's contract: they are returned verbatim as <c>refusalStage</c>, so
/// renaming one is a breaking change to callers.
/// </summary>
/// <remarks>
/// A refusal reason on its own cannot say which URL earned it - an <c>http://</c> Location
/// and an <c>http://</c> URL from the caller both yield
/// <see cref="RefusalReasons.SchemeNotHttps"/>. This is the field that separates them.
/// </remarks>
internal static class RefusalStages
{
    #region Constants

    /// <summary>The URL the caller supplied was refused; no hop had been followed.</summary>
    public const string Request = "request";

    /// <summary>A redirect the server sent was refused.</summary>
    public const string Redirect = "redirect";

    #endregion
}
