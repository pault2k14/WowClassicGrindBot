using Core;
using Core.GOAP;
using Core.Party;

using Microsoft.AspNetCore.Mvc;

using System;

namespace BlazorServer.Controllers;

/// <summary>
/// Exposes the leader/assist party-coordination API.
/// <list type="bullet">
///   <item><c>GET  /party/leader/state</c> — assist polls for the leader's live position and status.</item>
///   <item><c>POST /party/assist/state</c> — assist pushes its own state to the leader.</item>
///   <item><c>POST /party/debug/evade</c> — test-only: injects a synthetic
///   <see cref="EvadeBlacklistEvent"/> on the leader (using the leader's current
///   <c>PlayerReader.TargetGuid</c>) to exercise the blacklist + evade-recovery
///   pipeline end-to-end without needing a real in-game evade trigger.</item>
/// </list>
/// Rate limiting is performed on the client side via <see cref="PartyApiConfig"/>.
/// The endpoints themselves are stateless read/writes against singleton services.
/// </summary>
[ApiController]
[Route("party")]
public sealed class PartyController : ControllerBase
{
    /// <summary>
    /// Returns a fresh snapshot of the leader bot's current position, status, and health.
    /// Called by each assist at ~4 Hz (configurable via <see cref="PartyApiConfig.LeaderPollIntervalMs"/>).
    /// The snapshot is built on-demand from the live <see cref="Core.PlayerReader"/> — no caching.
    /// </summary>
    [HttpGet("leader/state")]
    public IActionResult GetLeaderState([FromServices] LeaderStateService leaderStateService)
    {
        return Ok(leaderStateService.GetCurrentState());
    }

    /// <summary>
    /// Stores the latest state snapshot for one assist bot.
    /// The leader's goals read from <see cref="AssistStateStore"/> in later steps instead
    /// of parsing chat messages for "i'm following" / "i tried following…".
    /// </summary>
    [HttpPost("assist/state")]
    public IActionResult PostAssistState(
        [FromServices] AssistStateStore store,
        [FromBody] AssistState state)
    {
        if (string.IsNullOrWhiteSpace(state.AssistId))
            return BadRequest("AssistId is required.");

        // Overwrite with an authoritative server-side timestamp so the leader's
        // AgeMs calculation is not distorted by clock skew between the two machines.
        state.Timestamp = DateTime.UtcNow;
        store.Update(state);
        return Ok();
    }

    /// <summary>
    /// Test-only endpoint: injects a synthetic <see cref="EvadeBlacklistEvent"/>
    /// into the leader's <see cref="GoapAgent"/> dispatcher, exercising the same
    /// code path a real evade in <see cref="Goals.ApproachTargetGoal"/> or
    /// <see cref="Goals.CombatGoal"/> would trigger.
    /// <para>
    /// The mob GUID is read from the leader's current target
    /// (<c>PlayerReader.TargetGuid</c>) — mirroring the real evade path at
    /// <c>ApproachTargetGoal.cs:264</c> which dispatches
    /// <c>SendGoapEvent(new EvadeBlacklistEvent(playerReader.TargetGuid))</c>
    /// with the same source. The original design supplied the GUID in the URL,
    /// but the live addon-derived GUID format is impractical to enter manually,
    /// so the endpoint now resolves it server-side.
    /// </para>
    /// <para>
    /// What this verifies end-to-end on a live two-bot setup:
    /// <list type="number">
    ///   <item>Leader: <c>HandleGoapEvent</c> sets <c>_evadeRecoveryUntilUtc</c>,
    ///   sets <c>_evadeLeaderWaiting</c>, calls <c>stopMoving.Stop()</c>, and
    ///   calls <c>leaderNavProvider.AddBlacklistedMobGuid(guid)</c>.</item>
    ///   <item>Leader: next <c>GET /party/leader/state</c> includes the GUID in
    ///   <see cref="LeaderState.BlacklistedMobGuids"/>.</item>
    ///   <item>Assist: <c>FollowFocusGoal.Update</c> diff loop sees the new GUID,
    ///   calls <c>IgnoreTarget</c>, raises its own <see cref="EvadeBlacklistEvent"/>,
    ///   and sets <c>CantFollow=true</c>.</item>
    ///   <item>Both bots: <c>GoapKey.evadeRecovery</c> flips on/off as the 25-second
    ///   recovery window opens and expires.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Pass <c>?ghost=true</c> in the query string to exercise the ghost-combat
    /// branch instead — dispatches with <c>guid=0</c> regardless of the leader's
    /// current target (15-second recovery, no GUID added to the blacklist, API
    /// transport portion of the test is skipped).
    /// </para>
    /// <para>
    /// Always available, including in Release builds. The endpoint:
    /// <list type="bullet">
    ///   <item>requires the bot to be running in <see cref="Mode.PartyLeader"/>
    ///   mode (refuses with 409 otherwise — see <c>GoapAgent.RaiseDebugEvent</c>);</item>
    ///   <item>requires the bot to be started (refuses with 409 if
    ///   <c>BotController.GoapAgent</c> is null);</item>
    ///   <item>requires a current target (refuses with 409 if
    ///   <c>PlayerReader.TargetGuid == 0</c>) — unless <c>?ghost=true</c>;</item>
    ///   <item>does nothing destructive — it only triggers the same logic a real
    ///   evade would, which is bounded by the existing recovery-window timing.</item>
    /// </list>
    /// The party API is expected to bind to a non-public address; protect at the
    /// network layer if exposing beyond localhost / LAN.
    /// </para>
    /// </summary>
    /// <param name="ghost">If <c>true</c>, dispatch with <c>guid=0</c> to exercise
    /// the ghost-combat recovery branch instead of reading the current target.</param>
    [HttpPost("debug/evade")]
    public IActionResult PostDebugEvade(
        [FromServices] IBotController botController,
        [FromServices] PlayerReader playerReader,
        [FromQuery] bool ghost = false)
    {
        // Mirrors the access pattern in LeaderStateService.DetermineStatus,
        // which also reaches GoapAgent through IBotController.
        GoapAgent? agent = botController.GoapAgent;
        if (agent == null)
        {
            return Conflict("Bot is not started — GoapAgent is not yet available. " +
                "Start the bot before invoking this endpoint.");
        }

        int guid;
        if (ghost)
        {
            guid = 0;
        }
        else
        {
            // Read the current target the same way ApproachTargetGoal does at line 264.
            // Zero is the "no target" sentinel — refuse rather than dispatch a no-op
            // ghost-combat event, since the caller asked for the evade branch.
            guid = playerReader.TargetGuid;
            if (guid == 0)
            {
                return Conflict("No current target — select a mob (e.g. via Tab) before " +
                    "invoking this endpoint, or pass ?ghost=true to dispatch the " +
                    "ghost-combat branch with guid=0.");
            }
        }

        bool dispatched = agent.RaiseDebugEvent(new EvadeBlacklistEvent(guid));
        if (!dispatched)
        {
            // RaiseDebugEvent only refuses when classConfig.Mode != PartyLeader.
            // Reflect that in the HTTP response so the test driver gets a clear signal.
            return Conflict("Bot is not running in PartyLeader mode. " +
                "The leader-side blacklist + evade-recovery pipeline can only be " +
                "exercised on a leader process.");
        }

        return Ok(new
        {
            dispatched = true,
            guid,
            source = ghost ? "ghost-combat (forced guid=0)" : "leader's current target",
            note = ghost
                ? "Ghost-combat branch: 15s recovery window, no GUID added to blacklist."
                : "Evade branch: 25s recovery window, GUID added to leader blacklist " +
                  "and propagated to assist via /party/leader/state."
        });
    }
}
