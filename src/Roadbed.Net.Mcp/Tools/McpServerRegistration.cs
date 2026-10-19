namespace Roadbed.Net.Mcp.Tools;

using System;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The MCP registration: the server, its stdio transport and its one tool type. Held apart from
/// <c>Program</c> so a test can register the server exactly as the host does and read back the
/// tool list it advertises.
/// </summary>
internal static class McpServerRegistration
{
    #region Internal Methods

    /// <summary>
    /// Registers the MCP server over stdio with the <see cref="GetTools"/> tool type.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <returns>The same service collection.</returns>
    internal static IServiceCollection AddGetToolServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools([typeof(GetTools)]);

        return services;
    }

    #endregion
}
