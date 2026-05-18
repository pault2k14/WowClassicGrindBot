using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Party;

/// <summary>
/// Rate-limited state publisher for the leader/assist party API.
/// Registered as a singleton <see cref="IReader"/> called from
/// <see cref="AddonReader.Update"/> every addon frame.
/// Assist mode only: POSTs <see cref="AssistState"/> to the leader at most once
/// per <see cref="PartyApiConfig.AssistPostIntervalMs"/>.
/// Status is read from <see cref="AssistStatusProvider"/> which
/// <see cref="Goals.FollowFocusGoal"/> writes each tick.
/// </summary>
public sealed class PartyStatePublisher : IReader
{
    private readonly ILogger<PartyStatePublisher> logger;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly PartyModeProvider modeProvider;
    private readonly AssistStatusProvider assistStatusProvider;
    private readonly IPartyApiClient apiClient;
    private readonly PartyApiConfig config;
    private DateTime _lastPostUtc = DateTime.MinValue;
    private readonly CancellationToken _shutdownToken;

    public PartyStatePublisher(
        ILogger<PartyStatePublisher> logger,
        PlayerReader playerReader,
        AddonBits bits,
        PartyModeProvider modeProvider,
        AssistStatusProvider assistStatusProvider,
        IPartyApiClient apiClient,
        IOptions<PartyApiConfig> configOptions,
        CancellationTokenSource cts)
    {
        this.logger = logger;
        this.playerReader = playerReader;
        this.bits = bits;
        this.modeProvider = modeProvider;
        this.assistStatusProvider = assistStatusProvider;
        this.apiClient = apiClient;
        this.config = configOptions.Value;
        _shutdownToken = cts.Token;
    }

    public void Update(IAddonDataProvider reader)
    {
        if (modeProvider.BotMode != Mode.AssistFocus)
            return;

        if (_shutdownToken.IsCancellationRequested)
            return;

        var now = DateTime.UtcNow;
        if ((now - _lastPostUtc).TotalMilliseconds < config.AssistPostIntervalMs)
            return;

        _lastPostUtc = now;

        AssistState state = BuildSnapshot();

        CancellationToken token = _shutdownToken;
        _ = Task.Run(() => apiClient.PostAssistStateAsync(state, token), token);
    }

    public void Reset()
    {
        _lastPostUtc = DateTime.MinValue;
    }

    private AssistState BuildSnapshot()
    {
        Vector3 world = playerReader.WorldPos;
        Vector3 map = playerReader.MapPos;

        // Use the status written by FollowFocusGoal — it reflects the true nav state.
        // Fall back to Combat/Dead if not in FFG context.
        BotStatus status = assistStatusProvider.CurrentStatus;
        if (bits.Dead())
        {
            status = BotStatus.Dead;
        }
        else if (bits.Combat() && status == BotStatus.Following
                 && !assistStatusProvider.EvadeRecoveryActive)
        {
            // Normal-grind override: assist's status is Following but it's
            // taking hits. Report Combat so the leader's distance gate
            // pauses until CombatGoal (cost 4) preempts FFG (cost 19) and
            // OnExit clears the stale Following claim — typically within
            // one GOAP tick (~50 ms).
            //
            // Suppressed during evade-recovery: in that window CombatGoal
            // is precondition-blocked by evadeRecovery=false and cannot
            // preempt FFG, so FFG correctly keeps publishing Following while
            // the blacklisted mob's combat flag stays latched for the full
            // 25 s. Without the gate, the override masks the assist's
            // correct Following claim and the leader's diff loop sees the
            // assist as not-Following / not-Navigating → AssistIsNotFollowing
            // → FRG.OnGoapEvent → Abort. Observed in log 31:
            //   14:32:39:047  assist Entered Combat
            //   14:32:39:991  FFG: Reached follow position, status=Following
            //   14:32:44:673  leader briefly saw AssistIsFollowing=True
            //   14:32:44:906  next snapshot showed Combat → "assist truly
            //                  unavailable — aborting"
            //   14:32:44:906→14:33:08:567  leader sat motionless 24 s
            status = BotStatus.Combat;
        }

        return new AssistState
        {
            AssistId = config.AssistId,
            WorldX = world.X,
            WorldY = world.Y,
            WorldZ = world.Z,
            MapX = map.X,
            MapY = map.Y,
            Status = status,
            CantFollow = assistStatusProvider.CantFollow,
            HealthPercent = playerReader.HealthPercent(),
            InCombat = bits.Combat(),
            // Fix BF: route progress published symmetrically to leader's
            // TargetWaypoint broadcast — closes the same "position-without-
            // route-context" asymmetry on the assist→leader direction.
            AssistRouteIndex = assistStatusProvider.CurrentRouteIndex,
            Timestamp = DateTime.UtcNow
        };
    }
}
