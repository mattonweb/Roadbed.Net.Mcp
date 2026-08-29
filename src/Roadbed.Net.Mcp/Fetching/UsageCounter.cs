namespace Roadbed.Net.Mcp.Fetching;

using System.Threading;
using global::Roadbed.Net.Mcp.Configuration;

/// <summary>
/// Caps how many network calls this process will make.
/// </summary>
/// <remarks>
/// IN MEMORY MEANS PER PROCESS, NOT PER WALL-CLOCK HOUR. Under stdio the count resets
/// whenever the server is respawned, so what it actually bounds is one agent session. The
/// configuration key is named for that, and there is no rolling window here to imply
/// otherwise.
/// <para>
/// One unit is charged per <c>Get</c> call that reaches the network. Redirect hops inside a
/// call are free - the agent asked for one fetch, not for the chain the server sent it on -
/// and a URL refused during validation is never charged, because nothing was sent.
/// </para>
/// </remarks>
internal sealed class UsageCounter
{
    #region Private Fields

    private readonly int _limit;

    private int _used;

    #endregion

    #region Public Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="UsageCounter"/> class.
    /// </summary>
    /// <param name="config">The fetch configuration.</param>
    public UsageCounter(FetchConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        this._limit = config.MaxCallsPerSession;
    }

    #endregion

    #region Public Properties

    /// <summary>Gets the number of calls still available in this session.</summary>
    public int Remaining => Math.Max(0, this._limit - Volatile.Read(ref this._used));

    #endregion

    #region Public Methods

    /// <summary>
    /// Charges one call against the session budget.
    /// </summary>
    /// <returns><see langword="true"/> when the call may proceed.</returns>
    public bool TryConsume()
    {
        while (true)
        {
            var current = Volatile.Read(ref this._used);

            if (current >= this._limit)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref this._used, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    #endregion
}
