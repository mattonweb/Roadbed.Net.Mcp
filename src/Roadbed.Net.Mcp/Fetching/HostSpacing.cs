namespace Roadbed.Net.Mcp.Fetching;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using global::Roadbed.Net.Mcp.Configuration;

/// <summary>
/// Politeness state for one host: a minimum interval between requests, and a cooldown that
/// opens after repeated failures so a host that is already unhappy is left alone.
/// </summary>
/// <remarks>
/// SPACING STATE IS PER PROCESS, AND UNDER STDIO THAT MEANS PER AGENT. If several agents
/// each spawn their own server instance, each gets an independent limiter and the guarantee
/// is per-agent rather than global. Two agents scheduled apart is a scheduling answer to
/// that, not a technical one; see docs/decisions.md, decision 3. If concurrent fetching
/// agents ever become normal, this state has to move somewhere shared.
/// </remarks>
internal sealed class HostSpacing
{
    #region Private Fields

    private readonly ConcurrentDictionary<string, HostState> _hosts = new ConcurrentDictionary<string, HostState>(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _minInterval;
    private readonly TimeSpan _cooldown;
    private readonly int _failureThreshold;

    #endregion

    #region Public Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="HostSpacing"/> class.
    /// </summary>
    /// <param name="config">The fetch configuration.</param>
    /// <param name="timeProvider">The clock seam, so tests do not wait in real time.</param>
    public HostSpacing(FetchConfig config, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this._timeProvider = timeProvider;
        this._minInterval = TimeSpan.FromMilliseconds(config.MinHostIntervalMilliseconds);
        this._cooldown = TimeSpan.FromSeconds(config.CircuitCooldownSeconds);
        this._failureThreshold = config.CircuitFailureThreshold;
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Determines whether a host's circuit is currently open.
    /// </summary>
    /// <param name="host">The host name.</param>
    /// <returns><see langword="true"/> when the host is in cooldown and must not be contacted.</returns>
    public bool IsCircuitOpen(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (!this._hosts.TryGetValue(host, out var state))
        {
            return false;
        }

        lock (state)
        {
            return state.OpenUntil is { } until && this._timeProvider.GetUtcNow() < until;
        }
    }

    /// <summary>
    /// Waits until the configured interval has passed since the last request to this host,
    /// and reserves the slot for the caller.
    /// </summary>
    /// <param name="host">The host name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the caller may send.</returns>
    public async Task WaitTurnAsync(string host, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var state = this._hosts.GetOrAdd(host, static _ => new HostState());
        TimeSpan delay;

        lock (state)
        {
            var now = this._timeProvider.GetUtcNow();
            var earliest = state.NextAllowed ?? now;
            delay = earliest > now ? earliest - now : TimeSpan.Zero;

            // Reserve this host's next slot before releasing the lock, so two concurrent
            // callers queue behind each other instead of both reading the same "now".
            state.NextAllowed = (earliest > now ? earliest : now) + this._minInterval;
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, this._timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records a successful request, closing the host's circuit.
    /// </summary>
    /// <param name="host">The host name.</param>
    public void RecordSuccess(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var state = this._hosts.GetOrAdd(host, static _ => new HostState());

        lock (state)
        {
            state.ConsecutiveFailures = 0;
            state.OpenUntil = null;
        }
    }

    /// <summary>
    /// Records a failed request, opening the host's circuit once the threshold is reached.
    /// </summary>
    /// <param name="host">The host name.</param>
    public void RecordFailure(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var state = this._hosts.GetOrAdd(host, static _ => new HostState());

        lock (state)
        {
            state.ConsecutiveFailures++;

            if (state.ConsecutiveFailures >= this._failureThreshold)
            {
                state.OpenUntil = this._timeProvider.GetUtcNow() + this._cooldown;
                state.ConsecutiveFailures = 0;
            }
        }
    }

    #endregion

    #region Private Types

    private sealed class HostState
    {
        public DateTimeOffset? NextAllowed { get; set; }

        public DateTimeOffset? OpenUntil { get; set; }

        public int ConsecutiveFailures { get; set; }
    }

    #endregion
}
