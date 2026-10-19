namespace Roadbed.Net.Mcp.Tests;

using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Roadbed.Net.Mcp.Fetching;
using Roadbed.Net.Mcp.Tools;

/// <summary>
/// The MCP tool surface as the server advertises it: registered the way <c>Program</c> registers
/// it, read back from the server options, and compared to values written here by hand. Nothing
/// below reads its expected values from the container it is checking.
/// </summary>
[TestClass]
public sealed class ToolWiringTests
{
    #region Expected values

    // Copied by hand from the [Description] on GetTools.Get. A change to either side fails the test.
    private const string ExpectedDescription =
        "Retrieve one HTTPS URL and return the RAW response body - never a rendered DOM, so "
        + "content assembled by JavaScript will not be present. GET only: no POST, no authentication, "
        + "no cookies, no caching, and no link-following. The destination is validated before anything "
        + "is contacted and again at every redirect. Read 'outcome', not 'status': 'blocked' means a bot "
        + "wall answered, which is not the same as a page with nothing on it. Read 'finalUrl', not the "
        + "URL you asked for. On a refusal, 'refusalStage' says where it happened - 'redirect' means a hop "
        + "the server sent was declined, and 'refusedUrl' names that hop, which is NOT the URL you asked "
        + "for. 'redirectCount' counts hops FOLLOWED, so a refusal at the first hop reports 0: that does "
        + "not mean no redirect happened. Calls are capped per session.";

    #endregion

    #region Tool list

    [TestMethod]
    public void Server_AdvertisesExactlyOneTool()
    {
        Tool[] tools = ListTools();

        Assert.AreEqual(1, tools.Length);
    }

    [TestMethod]
    public void Tool_IsNamedGet()
    {
        Tool tool = ListTools().Single();

        Assert.AreEqual("Get", tool.Name);
    }

    [TestMethod]
    public void Tool_TakesOneRequiredStringUrl()
    {
        JsonElement schema = ListTools().Single().InputSchema;
        JsonElement properties = schema.GetProperty("properties");
        string[] propertyNames = properties.EnumerateObject().Select(property => property.Name).ToArray();
        JsonElement url = properties.GetProperty("url");
        string[] required = schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()!).ToArray();

        CollectionAssert.AreEqual(new[] { "url" }, propertyNames);
        Assert.AreEqual("string", url.GetProperty("type").GetString());
        CollectionAssert.AreEqual(new[] { "url" }, required);
    }

    [TestMethod]
    public void Tool_CarriesTheSourceDescription()
    {
        Tool tool = ListTools().Single();

        Assert.AreEqual(ExpectedDescription, tool.Description);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Registers the server as the host does and returns the tools it would list.
    /// </summary>
    /// <returns>The advertised tools.</returns>
    private static Tool[] ListTools()
    {
        ServiceCollection services = new();

        // Registered as Program registers it, so the SDK sees the parameter as injected rather
        // than as tool input. Never resolved here.
        services.AddSingleton<IFetchService, FetchService>();
        services.AddGetToolServer();

        using (ServiceProvider provider = services.BuildServiceProvider())
        {
            McpServerOptions options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
            Assert.IsNotNull(options.ToolCollection, "The server registered no tool collection at all.");

            return options.ToolCollection.Select(tool => tool.ProtocolTool).ToArray();
        }
    }

    #endregion
}
