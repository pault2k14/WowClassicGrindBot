using System;
using System.Numerics;

namespace Core.Party;

/// <summary>
/// Leader-side singleton. Builds a <see cref="LeaderState"/> snapshot on demand
/// from the live <see cref="PlayerReader"/> and <see cref="AddonBits"/> values.
/// <para>
/// No background update loop is required: <see cref="PlayerReader"/> is always
/// current because <see cref="AddonReader.Update"/> runs in the addon thread at
/// ~250 Hz. The controller calls <see cref="GetCurrentState"/> on each HTTP GET,
/// so the snapshot is always at most one addon-frame old.
/// </para>
/// </summary>
public sealed class LeaderStateService
{
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly IBotController botController;
    private readonly LeaderNavigationProvider leaderNavProvider;

    // Cache the last valid world position. When the addon briefly returns (0,0,0)
    // (e.g. during loading screens or bot deactivation), we return the cached position
    // rather than sending the assist a bogus ~4400y distance (sqrt(700²+4350²) ≈ 4406y).
    private Vector3 _lastValidWorldPos;

    public LeaderStateService(
        PlayerReader playerReader,
        AddonBits bits,
        IBotController botController,
        LeaderNavigationProvider leaderNavProvider)
    {
        this.playerReader = playerReader;
        this.bits = bits;
        this.botController = botController;
        this.leaderNavProvider = leaderNavProvider;
    }

    public LeaderState GetCurrentState()
    {
        Vector3 world = playerReader.WorldPos;

        // Validate — (0,0,0) means the addon has not yet provided real data
        // or has momentarily lost its feed (loading screen, logout transition).
        if (world.X == 0 && world.Y == 0)
        {
            world = _lastValidWorldPos; // fall back to last known good position
        }
        else
        {
            _lastValidWorldPos = world;
        }

        Vector3 map = playerReader.MapPos;

        return new LeaderState
        {
            WorldX = world.X,
            WorldY = world.Y,
            WorldZ = world.Z,
            MapX = map.X,
            MapY = map.Y,
            UIMapId = playerReader.UIMapId.Value,
            Status = DetermineStatus(),
            HealthPercent = playerReader.HealthPercent(),
            InCombat = bits.Combat(),
            TargetGuid = playerReader.TargetGuid,
            Timestamp = DateTime.UtcNow,

            // Waypoint sharing — only meaningful when leader is Patrolling.
            HasTargetWaypoint = leaderNavProvider.HasTargetWaypoint,
            TargetWaypointWorldX = leaderNavProvider.TargetWaypointWorldX,
            TargetWaypointWorldY = leaderNavProvider.TargetWaypointWorldY,

            // Approach-start anchor — only meaningful when leader is Approaching.
            HasApproachStart = leaderNavProvider.HasApproachStart,
            ApproachStartWorldX = leaderNavProvider.ApproachStartWorldX,
            ApproachStartWorldY = leaderNavProvider.ApproachStartWorldY,

            // Mob blacklist — cumulative for the session.
            BlacklistedMobGuids = leaderNavProvider.BlacklistedMobGuidsSnapshot,

            // Fix AV (Route A): dynamic stuck-rect propagation. The
            // assist applies each entry via Navigation.AddPropagatedStuckRect
            // every poll cycle — duplicates are deduped on overlap and
            // refresh a TTL so the rects stay alive on the assist as long
            // as the leader keeps republishing them. Empty array when the
            // leader has no current dynamic rects (between plan transitions).
            StuckRects = leaderNavProvider.StuckRectsSnapshot,
        };
    }

    private BotStatus DetermineStatus()
    {
        if (bits.Dead())
            return BotStatus.Dead;

        if (bits.Combat())
            return BotStatus.Combat;

        if (!botController.IsBotActive)
            return BotStatus.Waiting;

        // Fix T (log-63 19:47:02 → 19:48:21): when FRG is actively paused waiting
        // for the assist (too far / Stuck / CantFollow), the bot is functionally
        // stationary even though its current goal is still FollowRouteGoal by
        // name. The previous goal-name → BotStatus mapping below reported
        // Patrolling in this state, which kept the assist's FFG in waypoint-
        // sharing mode targeting the leader's last-published waypoint. If that
        // waypoint had become unreachable (e.g., the leader just inserted a
        // detour around a blacklist rect — see Fix S), the assist's pathfinder
        // failed on every retry and the bots stayed separated until something
        // external (here, the bot stopping at 19:48:21:068) flipped status off
        // Patrolling. Reporting Waiting during pause-for-assist short-circuits
        // FFG's waypoint-sharing branch (which gates on
        // `leader.Status == Patrolling`) and routes the assist to the leader's
        // actual body via position-chase, which is the correct target while
        // the leader is stationary.
        if (leaderNavProvider.IsPausedForAssist)
            return BotStatus.Waiting;

        // ── Fix CN (log-114 evidence) — goal-name string mismatch ──
        //
        // The original switch keys used CLASS NAMES (e.g. "ApproachTargetGoal",
        // "LootGoal", "FollowRouteGoal"). But the actual GoapGoal.Name VALUE,
        // produced by the base ctor `: base(nameof(XxxGoal))` plus the base
        // class's display-formatting (strip "Goal" suffix + CamelCase-split),
        // is the HUMAN-READABLE form: "Approach Target", "Loot", etc. The
        // class-name keys NEVER matched goalName, so the switch always fell
        // through to the default `_ => BotStatus.Patrolling`.
        //
        // Evidence (log-114 LeaderStatePoller events received by assist):
        //   23:15:34:367  Leader status changed: Patrolling
        //   23:15:44:385  Leader status changed: Combat
        //   23:15:48:802  Leader status changed: Patrolling
        //   23:16:00:920  Leader status changed: Combat
        //   23:16:05:842  Leader status changed: Patrolling
        //   ...
        // Status only ever toggled between Patrolling ↔ Combat. NEVER reached
        // Approaching, Looting, Skinning, Resting, or Evading. The leader was
        // demonstrably in ATG/PTG repeatedly (per its own goal-plan log lines
        // "New Plan= Approach Target", "New Plan= Pull Target"), and was
        // looting between combats — none of which surfaced to the assist.
        //
        // Consequence: the assist's FFG.GetNavigationTarget reads
        // leader.Status to decide between RouteWalk (when Patrolling) and
        // PositionChase/Anchor (other statuses). With status frozen at
        // Patrolling during the leader's ATG, the assist took the RouteWalk
        // branch and pushed route-waypoint targets — including waypoints
        // NORTH of the leader's actual position. Concrete log-114 example
        // at 23:15:56:164: assist had walked SE to <-717.94, -4179.03>
        // (2.4y from leader anchor <-718.63, -4181.18>). CK suppressed an
        // OnDestinationReached Idle and called GetNavigationTarget(leader),
        // which — because leader.Status was incorrectly Patrolling — returned
        // route waypoint <-710.94, -4167.58>. The assist then walked back
        // NORTH ~10y to chase the route waypoint, ending at <-711.42, -4170.02>
        // by 23:15:58:786. User-visible: "backwards movement and travel by the
        // assist after combat" / "during approach".
        //
        // Plan-log inspection confirms the actual Goal.Name values:
        //   "Approach Target", "Pull Target", "Combat", "Loot",
        //   "Consume Corpse", "Corpse Consumed", "Follow Focus",
        //   "Follow <route-filename>" (FRG, variable suffix).
        // Skinning/Drink/Eat/Rest/Evade goals follow the same base-class
        // formatting pattern (strip "Goal" suffix; single-word names produce
        // "Skinning", "Drink", "Eat", "Rest", "Evade").
        //
        // Fix: change the switch keys to the actual Name values. FRG's
        // variable filename suffix is handled by the default Patrolling
        // arm — which is the desired status for FRG anyway, so no special
        // case is required. FollowFocusGoal also produces "Follow Focus"
        // but FFG is mode-gated to AssistFocus and never runs on the leader,
        // so no collision with the FRG-style default Patrolling arm.
        // ── Fix CO (log-115 evidence) — Goal.Name has a leading space ──
        //
        // Fix CN updated the switch keys from class names ("ApproachTargetGoal")
        // to what we believed were the actual Name values ("Approach Target").
        // But the LeaderStatePoller log in log-115 still showed status broadcasts
        // toggling only between Patrolling and Combat — never Approaching, never
        // Looting. CN didn't take effect.
        //
        // Hex-dump of the assist plan log lines revealed that Goal.Name has a
        // LEADING SPACE for every goal:
        //   "New Plan= {name}" template produces "New Plan=  Approach Target"
        //   with TWO spaces between '=' and 'Approach'. Since the template
        //   contributes only one space, the value of `name` must be the string
        //   " Approach Target" (leading space included). Same for every other
        //   goal: " Combat", " Loot", " Follow Focus", and even the FRG name
        //   " Follow 01-04_ Durotar_ Valley of Trials" — including FRG which
        //   passes an explicit string ("Follow ..." without leading space) to
        //   the base ctor, confirming the leading space is added inside the
        //   base GoapGoal class regardless of how Name is derived.
        //
        // So the CN switch keys "Loot", "Approach Target" etc. (without leading
        // space) never matched the actual goalName values, and the switch fell
        // through to the default Patrolling arm — exactly the symptom the user
        // continued to report in log-115.
        //
        // Concrete evidence at 00:07:04-11 in log-115:
        //   00:07:04:571 LEADER plan: Approach Target  (Name = " Approach Target")
        //   00:07:05:943 ASSIST BE push: 11-waypoint route span heading NORTH
        //                from bot position <-716.04, -4189.87>. BE makes this
        //                push because FFG.GetNavigationTarget(leader) takes the
        //                RouteWalk branch when leader.Status == Patrolling —
        //                which it was, incorrectly, because the CN switch missed.
        //   00:07:07:698 Bot moved NORTH to <-716.27, -4180.93> (~9y N).
        //                Mode flips PositionChase → Anchor at <-717.06, -4190.48>
        //                (SOUTH of bot). Bot must walk SOUTH ~9.6y back.
        //   00:07:11:735 FFG.OnExit: assist at 35.3y from leader (lost worse).
        //
        // The polled status broadcasts during this whole sequence — 6.5 seconds
        // covering leader's ATG → PTG → ATG → NO PLAN → PTG → Combat cycle —
        // showed only Patrolling, then Combat at 00:07:11:620. No Approaching
        // status was ever broadcast, confirming the switch never matched.
        //
        // Fix: trim the goalName before the switch. The base GoapGoal class's
        // leading-space convention is now neutralized; the switch keys remain
        // the readable forms (no leading space hard-coded into source). If the
        // base class is ever fixed to omit the leading space, this code keeps
        // working unchanged.
        //
        // ── Update (Fix CP) ──
        // The base GoapGoal constructor was inspected after CO was deployed
        // (file `GoapGoal.cs`) and Fix CP was applied to strip the leading
        // space at the source. So `Name` no longer has a leading space and
        // the `.Trim()` below is now redundant. It is intentionally retained
        // as defensive belt-and-suspenders: if CP is ever reverted by
        // accident, the switch keeps working here. Cost is one Trim() call
        // per 250 ms poll cycle, negligible.
        string? goalName = botController.GoapAgent?.CurrentGoal?.Name?.Trim();
        return goalName switch
        {
            "Loot"               => BotStatus.Looting,
            "Skinning"           => BotStatus.Skinning,
            "Drink"
                or "Eat"
                or "Rest"        => BotStatus.Resting,
            "Evade"              => BotStatus.Evading,
            "Approach Target"
                or "Pull Target" => BotStatus.Approaching,
            _                    => BotStatus.Patrolling
        };
    }
}
