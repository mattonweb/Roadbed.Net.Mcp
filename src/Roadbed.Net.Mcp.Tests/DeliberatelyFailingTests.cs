// DELIBERATELY FAILING TEST. Exists only on the scratch branch feature/pull-request-checks-red, to prove the
// pull-request-checks workflow turns a pull request red. Never merge this branch; delete it after the check.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Roadbed.Net.Mcp.Tests;

[TestClass]
public sealed class DeliberatelyFailingTests
{
    [TestMethod]
    public void PullRequestChecks_WhenATestFails_TurnsThePullRequestRed()
    {
        Assert.Fail("Deliberate failure for the pull-request-checks commissioning run. Do not merge.");
    }
}
