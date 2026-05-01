using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Party;

/// <summary>
/// Assist-side singleton <see cref="IReader"/> that polls
/// <c>GET /party/leader/state</c> at most once per
/// <see cref="PartyApiConfig.LeaderPollIntervalMs"/>.
/// <para>
/// Registered as an <see cref="IReader"/> so it is called automatically
/// from the <c>readers.AsSpan()</c> loop in <see cref="AddonReader.Update"/>
/// on the addon thread. The HTTP GET itself runs on a thread-pool task so the
/// addon thread is never blocked. Results are stored in
/// <see cref="LeaderConnectionStatus"/>, which <see cref="Goals.FollowFocusGoal"/>
/// and <see cref="GOAP.GoapAgent"/> read.
/// </para>
/// </summary>
public sealed class LeaderStatePoller : IReader
{
    private readonly ILogger<LeaderStatePoller> logger;
    private readonly IPartyApiClient apiClient;
    private readonly LeaderConnectionStatus leaderConnection;
    private readonly PartyModeProvider modeProvider;
    private readonly int pollIntervalMs;
    private readonly CancellationToken _shutdownToken;

    private DateTime _lastPollUtc = DateTime.MinValue;
    private int _pollInFlight;

    // Only log when something meaningful changes on the leader side.
    private BotStatus _lastLoggedStatus = (BotStatus)(-1); // sentinel: never matched
    private bool _hasLoggedFirstConnection;

    public LeaderStatePoller(
        ILogger<LeaderStatePoller> logger,
        IPartyApiClient apiClient,
        LeaderConnectionStatus leaderConnection,
        PartyModeProvider modeProvider,
        IOptions<PartyApiConfig> configOptions,
        CancellationTokenSource cts)
    {
        this.logger = logger;
        this.apiClient = apiClient;
        this.leaderConnection = leaderConnection;
        this.modeProvider = modeProvider;
        this.pollIntervalMs = configOptions.Value.LeaderPollIntervalMs;
        this._shutdownToken = cts.Token;
    }

    /// <summary>
    /// Called by <see cref="AddonReader.Update"/> on every addon frame.
    /// No-op on the leader bot or before a profile is loaded.
    /// </summary>
    public void Update(IAddonDataProvider reader)
    {
        if (modeProvider.BotMode != Mode.AssistFocus)
            return;

        if (_shutdownToken.IsCancellationRequested)
            return;

        var now = DateTime.UtcNow;
        if ((now - _lastPollUtc).TotalMilliseconds < pollIntervalMs)
            return;

        // Don't spawn a new task if the previous GET hasn't finished.
        if (Interlocked.CompareExchange(ref _pollInFlight, 1, 0) != 0)
            return;

        _lastPollUtc = now;

        CancellationToken token = _shutdownToken;
        _ = Task.Run(async () =>
        {
            try
            {
                LeaderState? state = await apiClient.GetLeaderStateAsync(token)
                    .ConfigureAwait(false);

                if (state != null)
                {
                    leaderConnection.UpdateLeaderState(state);

                    // Only log on first contact or when the leader's status changes —
                    // polling at 4 Hz would otherwise flood the log with identical lines.
                    if (!_hasLoggedFirstConnection || state.Status != _lastLoggedStatus)
                    {
                        _hasLoggedFirstConnection = true;
                        _lastLoggedStatus = state.Status;
                        logger.LogInformation(
                            $"[LeaderStatePoller] Leader status changed: {state.Status} " +
                            $"health={state.HealthPercent}% " +
                            $"mapPos=({state.MapX:0.00},{state.MapY:0.00}) " +
                            $"localAge={leaderConnection.LocalAgeMs:0}ms");
                    }
                }
                else
                {
                    leaderConnection.RecordPollFailure();
                }
            }
            catch (OperationCanceledException)
            {
                // Normal during shutdown.
            }
            catch (Exception ex)
            {
                leaderConnection.RecordPollFailure(ex);
            }
            finally
            {
                Interlocked.Exchange(ref _pollInFlight, 0);
            }
        }, token);
    }

    /// <summary>
    /// Called by <see cref="AddonReader.FullReset"/>.
    /// Reset the rate-limit timer so polling resumes immediately after reset.
    /// </summary>
    public void Reset()
    {
        _lastPollUtc = DateTime.MinValue;
    }
}
