using Core.GOAP;
using Microsoft.Extensions.Logging;

namespace Core.Goals;

public sealed class TargetFocusTargetGoal : GoapGoal, IGoapEventListener
{
    public override float Cost => 10f;

    private readonly ILogger<TargetFocusTargetGoal> logger;
    private readonly ConfigurableInput input;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly Wait wait;
    private readonly ChatReader chatReader;

    // Set by OnGoapEvent when GoapAgent broadcasts evadeRecovery=true/false.
    // While true, CanRun() returns false so the planner cannot select this goal.
    // This prevents the TFT/FFG ping-pong that occurs when bits.Focus_Combat() is
    // true (the leader's focus is on an evading mob) during evade recovery — without
    // this guard, TFT beats FFG every plan cycle because FFG presses N4 on exit and
    // TFT has no cost penalty, starving FFG of the plan-holding time it needs to
    // navigate back to the leader.
    private bool _evadeRecoveryActive;

    public TargetFocusTargetGoal(ConfigurableInput input, PlayerReader playerReader,
        AddonBits bits, ClassConfiguration classConfig, Wait wait,
        ILogger<TargetFocusTargetGoal> logger, ChatReader chatReader)
        : base(nameof(TargetFocusTargetGoal))
    {
        this.input = input;
        this.playerReader = playerReader;
        this.bits = bits;
        this.wait = wait;
        this.chatReader = chatReader;

        /* This was preventing AssistFocus mode from returning to combat 
         *  when combat is temporarily left for other plans. Seen when 2 or 3
            mobs attack at the same time.
            [GoapAgent        ] New Plan= NO PLAN
            appears in the log */
        /*
        if (classConfig.Loot)
        {
            AddPrecondition(GoapKey.incombat, false);
        }
        */

        AddPrecondition(GoapKey.forcedfollow, false);
        AddPrecondition(GoapKey.hasfocus, true);
        AddPrecondition(GoapKey.focushastarget, true);
        this.logger = logger;
    }

    public void OnGoapEvent(GoapEventArgs e)
    {
        if (e is GoapStateEvent s && s.Key == GoapKey.evadeRecovery)
        {
            _evadeRecoveryActive = s.Value;
            if (s.Value)
                logger.LogInformation("[TFT] Evade recovery started — CanRun() blocked until recovery clears.");
            else
                logger.LogInformation("[TFT] Evade recovery cleared — CanRun() unblocked.");
        }
    }

    public override bool CanRun()
    {
        // During evade recovery the assist must stay in FollowFocusGoal and
        // navigate back to the leader. Returning false here prevents the planner
        // from ever selecting TFT, stopping the TFT/FFG ping-pong loop.
        if (_evadeRecoveryActive)
            return false;

        if (bits.TargetTarget_PlayerOrPet())
            return false;

        // If the focus's target is in playerReader.IsIgnored (just blacklisted via
        // an evade event — agent-level diff on assist, real-evade call sites on
        // leader, or the new HandleGoapEvent IgnoreTarget call covering the test
        // endpoint), do NOT re-acquire it via PageUp+F. Doing so triggers a tight
        // ping-pong: TFT acquires the blacklisted GUID via the focus chain → plan
        // switches to Combat → CombatGoal's session-24 IsIgnored short-circuit
        // (CombatGoal.cs:198) bounces it → plan back to TFT → loop. Three full
        // cycles observed on the assist in log 25 (23:01:51:534, 23:01:51:996,
        // 23:01:52:476) before CombatGoal.FindPossibleThreats's Tab path finally
        // caught it and re-fired EvadeBlacklistEvent at 23:01:52:910 — starting
        // a fresh 25 s evade-recovery window on top of the first one's tail.
        //
        // Check FocusTargetGuid != 0 directly rather than gating on bits.FocusTarget().
        // The addon's bits and FocusTargetGuid are read independently and can briefly
        // disagree — a race window where CanRun returns true while FocusTargetGuid
        // is still set to the blacklisted GUID. The GUID-based check is authoritative
        // and avoids that race.
        if (playerReader.FocusTargetGuid != 0 && playerReader.IsIgnored(playerReader.FocusTargetGuid))
            return false;

        return
            (bits.FocusTarget_Hostile() && bits.FocusTarget_Combat()) ||
            !bits.FocusTarget_Hostile();
    }

    public override void OnEnter()
    {
        input.PressTargetFocus();
        wait.Update();
    }

    public override void Update()
    {
        if (bits.Drowning())
        {
            input.PressJump();
        }

        if (chatReader.ForcedFollow)
        {
            AddEffect(GoapKey.forcedfollow, true);
            return;
        }

        if (bits.FocusTarget_Hostile())
        {
            if (bits.FocusTarget_Combat())
            {
                // Defense in depth — same check as CanRun(). The two are read at
                // different points (plan time vs. Update time) and the focus's
                // target can change between them. Without this guard, an Update
                // tick that begins while FocusTargetGuid points at a blacklisted
                // GUID will press F (TargetTargetOfTarget) and acquire the
                // blacklisted mob, defeating the CanRun gate.
                if (playerReader.FocusTargetGuid != 0 &&
                    playerReader.IsIgnored(playerReader.FocusTargetGuid))
                {
                    wait.Update();
                    return;
                }

                input.PressTargetFocus();
                input.PressTargetOfTarget();
            }
        }
        else if (playerReader.SpellInRange.FocusTarget_Trade)
        {
            logger.LogInformation("TargetFocusTargetGoal: FocusTarget Not Hostile, Pressing Interact");
            input.PressTargetFocus();
            input.PressTargetOfTarget();
            input.PressInteract();
        }

        wait.Update();
    }

    public override void OnExit()
    {
        if (!bits.FocusTarget())
        {
            input.PressClearTarget();
            wait.Update();
        }
    }
}
