namespace Roadbed.Net.Mcp.Tests;

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Roadbed.Net.Mcp.Configuration;
using Roadbed.Net.Mcp.Fetching;

/// <summary>
/// The per-process state: the session cap, host spacing and the circuit.
/// </summary>
[TestClass]
public sealed class PolicyStateTests
{
    #region Usage cap

    [TestMethod]
    public void UsageCounter_StopsAtTheLimit()
    {
        var counter = new UsageCounter(new FetchConfig { MaxCallsPerSession = 3 });

        Assert.IsTrue(counter.TryConsume());
        Assert.IsTrue(counter.TryConsume());
        Assert.IsTrue(counter.TryConsume());
        Assert.IsFalse(counter.TryConsume());
        Assert.IsFalse(counter.TryConsume());
        Assert.AreEqual(0, counter.Remaining);
    }

    [TestMethod]
    public void UsageCounter_DefaultsToTheRuledFifty()
    {
        // Sized to the batch, not to a round number: roughly 4-5 calls per subject over
        // about ten subjects in a scheduled session.
        Assert.AreEqual(50, new FetchConfig().MaxCallsPerSession);
    }

    #endregion

    #region Host spacing

    [TestMethod]
    public async Task Spacing_MakesTheSecondRequestToAHostWait()
    {
        var spacing = new HostSpacing(
            new FetchConfig { MinHostIntervalMilliseconds = 150 },
            TimeProvider.System);

        await spacing.WaitTurnAsync("example.com");

        var stopwatch = Stopwatch.StartNew();
        await spacing.WaitTurnAsync("example.com");
        stopwatch.Stop();

        Assert.IsTrue(
            stopwatch.ElapsedMilliseconds >= 100,
            $"The second turn waited only {stopwatch.ElapsedMilliseconds}ms.");
    }

    [TestMethod]
    public async Task Spacing_IsPerHost()
    {
        var spacing = new HostSpacing(
            new FetchConfig { MinHostIntervalMilliseconds = 5000 },
            TimeProvider.System);

        await spacing.WaitTurnAsync("one.example.com");

        var stopwatch = Stopwatch.StartNew();
        await spacing.WaitTurnAsync("two.example.com");
        stopwatch.Stop();

        Assert.IsTrue(stopwatch.ElapsedMilliseconds < 1000, "A different host should not have waited.");
    }

    #endregion

    #region Circuit

    [TestMethod]
    public void Circuit_OpensAtTheThresholdAndOnlyForThatHost()
    {
        var spacing = new HostSpacing(
            new FetchConfig { CircuitFailureThreshold = 3, CircuitCooldownSeconds = 60 },
            TimeProvider.System);

        spacing.RecordFailure("flaky.example.com");
        spacing.RecordFailure("flaky.example.com");
        Assert.IsFalse(spacing.IsCircuitOpen("flaky.example.com"));

        spacing.RecordFailure("flaky.example.com");
        Assert.IsTrue(spacing.IsCircuitOpen("flaky.example.com"));
        Assert.IsFalse(spacing.IsCircuitOpen("fine.example.com"));
    }

    [TestMethod]
    public void Circuit_SuccessClearsTheRunOfFailures()
    {
        var spacing = new HostSpacing(
            new FetchConfig { CircuitFailureThreshold = 3, CircuitCooldownSeconds = 60 },
            TimeProvider.System);

        spacing.RecordFailure("example.com");
        spacing.RecordFailure("example.com");
        spacing.RecordSuccess("example.com");
        spacing.RecordFailure("example.com");
        spacing.RecordFailure("example.com");

        Assert.IsFalse(spacing.IsCircuitOpen("example.com"));
    }

    [TestMethod]
    public void Circuit_CooldownExpires()
    {
        var spacing = new HostSpacing(
            new FetchConfig { CircuitFailureThreshold = 1, CircuitCooldownSeconds = 0 },
            TimeProvider.System);

        spacing.RecordFailure("example.com");

        Assert.IsFalse(spacing.IsCircuitOpen("example.com"), "A zero cooldown is already over.");
    }

    #endregion
}
