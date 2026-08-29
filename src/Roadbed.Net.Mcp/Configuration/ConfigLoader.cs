namespace Roadbed.Net.Mcp.Configuration;

using System;
using System.IO;
using System.Text.Json;

/// <summary>
/// Loads <see cref="FetchConfig"/> from <c>.Roadbed.Net.Mcp</c> in the current user's home
/// directory, outside the agent's workspace.
/// </summary>
/// <remarks>
/// The file is optional. This server holds no credentials and needs none, so an absent file
/// means the ruled defaults, not a startup failure. A file that exists but is malformed does
/// fail startup - a half-read policy is worse than no server.
/// </remarks>
public static class ConfigLoader
{
    #region Private Fields

    private const string ConfigFileName = ".Roadbed.Net.Mcp";

    private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    #endregion

    #region Public Methods

    /// <summary>
    /// Resolves the configuration file path: <c>.Roadbed.Net.Mcp</c> in the user's home directory.
    /// </summary>
    /// <returns>The resolved absolute path, which may not exist.</returns>
    public static string ResolvePath()
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userHome, ConfigFileName);
    }

    /// <summary>
    /// Loads configuration from the resolved path, falling back to the ruled defaults.
    /// </summary>
    /// <returns>The validated configuration.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a present file cannot be parsed or fails validation.</exception>
    public static FetchConfig Load()
    {
        return LoadFromFile(ResolvePath());
    }

    /// <summary>
    /// Loads configuration from an explicit path, falling back to the ruled defaults.
    /// </summary>
    /// <param name="path">The configuration file path.</param>
    /// <returns>The validated configuration.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a present file cannot be parsed or fails validation.</exception>
    public static FetchConfig LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        FetchConfig config;

        if (File.Exists(path))
        {
            FetchConfig? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<FetchConfig>(File.ReadAllText(path), ReadOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Configuration file at '{path}' is not valid JSON: {ex.Message}");
            }

            config = parsed ?? throw new InvalidOperationException(
                $"Configuration file at '{path}' parsed to nothing.");
        }
        else
        {
            config = new FetchConfig();
        }

        config.Validate();
        return config;
    }

    #endregion
}
