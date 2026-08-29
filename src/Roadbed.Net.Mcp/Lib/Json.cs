namespace Roadbed.Net.Mcp.Lib;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Serialization for the MCP boundary. One shared options instance: System.Text.Json keys
/// its reflection metadata cache by options instance, so building one per call is the
/// classic way to throw the cache away on every request.
/// </summary>
public static class Json
{
    #region Private Fields

    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    #endregion

    #region Public Methods

    /// <summary>
    /// Serializes a value to a compact JSON string, omitting null members.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value to serialize.</param>
    /// <returns>The compact JSON representation.</returns>
    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, Options);
    }

    #endregion
}
