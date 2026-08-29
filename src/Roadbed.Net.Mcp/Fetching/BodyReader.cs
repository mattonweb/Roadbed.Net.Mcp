namespace Roadbed.Net.Mcp.Fetching;

using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Reads a response body up to the size cap and turns it into the single <c>body</c> string.
/// </summary>
/// <remarks>
/// What comes out is the RAW response. Nothing here parses, rewrites or renders it: textual
/// content types are decoded to text, everything else is base64-encoded with its declared
/// content type left intact, and the caller decides what any of it means.
/// </remarks>
internal static class BodyReader
{
    #region Private Fields

    private const int ChunkSize = 8192;

    #endregion

    #region Internal Methods

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> of the response body.
    /// </summary>
    /// <param name="response">The response to read.</param>
    /// <param name="maxBytes">The size cap in bytes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The body text, the byte count read, whether the cap stopped the read, and whether the body was textual.</returns>
    public static async Task<BodyReadResult> ReadAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        var bytes = await ReadCappedAsync(response, maxBytes, cancellationToken).ConfigureAwait(false);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var isTextual = IsTextual(mediaType);

        var text = isTextual
            ? ResolveEncoding(response.Content.Headers.ContentType?.CharSet).GetString(bytes.Payload)
            : Convert.ToBase64String(bytes.Payload);

        return new BodyReadResult(text, bytes.Payload.Length, bytes.Truncated, isTextual);
    }

    /// <summary>
    /// Determines whether a declared media type is one this server decodes as text.
    /// </summary>
    /// <param name="mediaType">The declared media type, or null.</param>
    /// <returns><see langword="true"/> when the body is decoded rather than base64-encoded.</returns>
    public static bool IsTextual(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            // Nothing declared. Treat it as text: the overwhelming majority of undeclared
            // bodies are markup, and a caller can always tell from the content itself.
            return true;
        }

        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/ecmascript", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Private Methods

    private static async Task<(byte[] Payload, bool Truncated)> ReadCappedAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        using var buffer = new MemoryStream();
        var chunk = new byte[ChunkSize];

        while (buffer.Length < maxBytes)
        {
            var want = (int)Math.Min(ChunkSize, maxBytes - buffer.Length);
            var read = await stream.ReadAsync(chunk.AsMemory(0, want), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return (buffer.ToArray(), false);
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        // The cap was reached. One more read says whether anything was actually left behind,
        // which is the difference between a body that exactly fits and one that was cut.
        var extra = await stream.ReadAsync(chunk.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        return (buffer.ToArray(), extra > 0);
    }

    private static Encoding ResolveEncoding(string? charSet)
    {
        if (string.IsNullOrWhiteSpace(charSet))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charSet.Trim().Trim('"'));
        }
        catch (ArgumentException)
        {
            // An unknown or unregistered code page. UTF-8 with replacement characters keeps
            // the body readable rather than failing a fetch over a header we do not trust.
            return Encoding.UTF8;
        }
    }

    #endregion
}

/// <summary>
/// The outcome of reading one response body.
/// </summary>
internal sealed class BodyReadResult
{
    #region Public Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="BodyReadResult"/> class.
    /// </summary>
    /// <param name="body">The decoded text, or the base64 payload when the body is binary.</param>
    /// <param name="byteCount">The number of body bytes read off the wire.</param>
    /// <param name="truncated">Whether the size cap stopped the read.</param>
    /// <param name="isTextual">Whether the body was decoded as text.</param>
    public BodyReadResult(string body, int byteCount, bool truncated, bool isTextual)
    {
        this.Body = body;
        this.ByteCount = byteCount;
        this.Truncated = truncated;
        this.IsTextual = isTextual;
    }

    #endregion

    #region Public Properties

    /// <summary>Gets the decoded text, or the base64 payload when the body is binary.</summary>
    public string Body { get; }

    /// <summary>Gets the number of body bytes read off the wire.</summary>
    public int ByteCount { get; }

    /// <summary>Gets a value indicating whether the size cap stopped the read.</summary>
    public bool Truncated { get; }

    /// <summary>Gets a value indicating whether the body was decoded as text.</summary>
    public bool IsTextual { get; }

    #endregion
}
