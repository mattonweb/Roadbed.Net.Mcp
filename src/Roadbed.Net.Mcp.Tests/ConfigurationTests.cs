namespace Roadbed.Net.Mcp.Tests;

using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Roadbed.Net.Mcp.Configuration;

/// <summary>
/// The ruled defaults, and what the configuration file may and may not do to them.
/// </summary>
[TestClass]
public sealed class ConfigurationTests
{
    #region Defaults

    [TestMethod]
    public void RuledDefaults()
    {
        var config = new FetchConfig();

        Assert.AreEqual(50, config.MaxCallsPerSession);
        Assert.AreEqual(10 * 1024 * 1024, config.MaxResponseBytes);
        Assert.AreEqual(30, config.RequestTimeoutSeconds);
        Assert.AreEqual(5, config.MaxRedirects);
    }

    [TestMethod]
    public void DefaultUserAgent_IsSelfIdentifying_NotImpersonating()
    {
        // The shipped posture: browser-compatible enough to clear WAFs that block bare
        // framework agents, while still telling a publisher who we are and how to reach us
        // before they decide to block us.
        var agent = new FetchConfig
        {
            ProductName = "Example.Agent",
            ProductVersion = "2.1",
            ContactUrl = "https://example.com/bots",
            ContactEmail = "bots@example.com",
        }.ResolveUserAgent();

        Assert.AreEqual(
            "Mozilla/5.0 (compatible; Example.Agent/2.1; +https://example.com/bots; bots@example.com)",
            agent);
    }

    [TestMethod]
    public void DefaultUserAgent_StillIdentifiesWithoutContactDetails()
    {
        var agent = new FetchConfig { ProductName = "Example.Agent", ProductVersion = "2.1" }.ResolveUserAgent();

        Assert.AreEqual("Mozilla/5.0 (compatible; Example.Agent/2.1)", agent);
    }

    [TestMethod]
    public void ImpersonationIsPossibleButOnlyByExplicitConfiguration()
    {
        // It forfeits the contactable half of the default and does not clear challenge-based
        // protections anyway. It belongs to the owner of a consuming repo, and it is never
        // this repo's default - which is why it takes a verbatim override to get here.
        const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko)";

        Assert.AreEqual(Chrome, new FetchConfig { UserAgent = Chrome }.ResolveUserAgent());
        StringAssert.Contains(new FetchConfig().ResolveUserAgent(), "Roadbed.Net.Mcp");
    }

    #endregion

    #region Loading

    [TestMethod]
    public void AbsentFile_MeansTheDefaults_NotAFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), "roadbed-net-mcp-" + Guid.NewGuid().ToString("N"));

        var config = ConfigLoader.LoadFromFile(path);

        Assert.AreEqual(50, config.MaxCallsPerSession);
    }

    [TestMethod]
    public void ConfiguredValuesOverrideTheDefaults()
    {
        var path = WriteTempConfig("""
            {
              "maxCallsPerSession": 12,
              "maxResponseBytes": 2097152,
              "contactEmail": "bots@example.com"
            }
            """);

        try
        {
            var config = ConfigLoader.LoadFromFile(path);

            Assert.AreEqual(12, config.MaxCallsPerSession);
            Assert.AreEqual(2097152, config.MaxResponseBytes);
            StringAssert.Contains(config.ResolveUserAgent(), "bots@example.com");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void MalformedFile_FailsStartupRatherThanHalfApplying()
    {
        var path = WriteTempConfig("{ not json");

        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => ConfigLoader.LoadFromFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void UnworkableValue_IsRejected()
    {
        var path = WriteTempConfig("""{ "maxCallsPerSession": 0 }""");

        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => ConfigLoader.LoadFromFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    #endregion

    #region Private Methods

    private static string WriteTempConfig(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "roadbed-net-mcp-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, json);
        return path;
    }

    #endregion
}
