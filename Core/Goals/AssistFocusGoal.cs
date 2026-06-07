using Core.GOAP;

using Microsoft.Extensions.Logging;

using System.Diagnostics;

namespace Core.Goals;

public sealed class AssistFocusGoal : GoapGoal
{
    private readonly ILogger<AssistFocusGoal> logger;
    private readonly ConfigurableInput input;
    private readonly ClassConfiguration classConfig;
    private readonly Wait wait;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly StopMoving stopMoving;
    private readonly CastingHandler castingHandler;
    private readonly IMountHandler mountHandler;
    private readonly CombatLog combatLog;
    private readonly RestHandler restHandler;
    private readonly ChatReader chatReader;

    public AssistFocusGoal(ILogger<AssistFocusGoal> logger,
        ConfigurableInput input,
        ClassConfiguration classConfig,
        Wait wait,
        PlayerReader playerReader,
        AddonBits bits,
        StopMoving stopMoving,
        CastingHandler castingHandler,
        IMountHandler mountHandler,
        CombatLog combatLog,
        RestHandler restHandler,
        ChatReader chatReader
        )
        : base(nameof(AssistFocusGoal))
    {
        this.logger = logger;
        this.input = input;
        this.classConfig = classConfig;
        this.wait = wait;
        this.playerReader = playerReader;
        this.bits = bits;
        this.stopMoving = stopMoving;
        this.castingHandler = castingHandler;
        this.mountHandler = mountHandler;
        this.combatLog = combatLog;
        this.restHandler = restHandler;
        this.chatReader = chatReader;

        this.Keys = classConfig.AssistFocus.Sequence;

        AddPrecondition(GoapKey.forcedfollow, false);
        AddPrecondition(GoapKey.hasfocus, true);
        AddPrecondition(GoapKey.focusconnected, true);

    }

    public override float Cost => 3.9f;

    public override bool CanRun()
    {
        for (int i = 0; i < Keys.Length; i++)
        {
            KeyAction key = Keys[i];
            if (key.CanRun())
                return true;
        }

        return false;
    }

    public override void OnEnter()
    {
        wait.Update();
        input.PressTargetFocus();
        wait.Update();

        while (restHandler.IsResting())
        {
            wait.Update(1000);
        }
    }

    public override void OnExit()
    {
        wait.Update();
        input.PressClearTarget();
        wait.Update();
    }

    public override void Update()
    {
        // ── Fix GK (run-169 extension) — AssistFocusGoal Update-entry wait timing ──
        // See FollowFocusGoal.cs Fix GK block (~line 3326) for full writeup.
        long gkStart = Stopwatch.GetTimestamp();
        wait.Update();
        double gkElapsedMs = Stopwatch.GetElapsedTime(gkStart).TotalMilliseconds;
        if (gkElapsedMs > 5000)
        {
            logger.LogWarning(
                $"[AssistFocusGoal] [FIX-FIRE] GK: Update-entry wait.Update() blocked for " +
                $"{gkElapsedMs:0}ms (>5000ms threshold). Likely cause: WoW addon " +
                $"GlobalTime counter stalled. State at unblock: " +
                $"TargetGuid={playerReader.TargetGuid} " +
                $"FocusTargetGuid={playerReader.FocusTargetGuid} " +
                $"bits.Combat={bits.Combat()} " +
                $"Mode={classConfig.Mode}.");
        }

        if (bits.Drowning())
        {
            input.PressJump();
            return;
        }

        for (int i = 0; bits.Target_Alive() && i < Keys.Length; i++)
        {
            if (chatReader.ForcedFollow)
            {
                AddEffect(GoapKey.forcedfollow, true);
                return;
            }

            KeyAction keyAction = Keys[i];

            if (castingHandler.SpellInQueue() && !keyAction.BaseAction)
            {
                continue;
            }

            if (keyAction.BeforeCastDismount && mountHandler.IsMounted())
            {
                mountHandler.Dismount();
            }

            if (castingHandler.CastIfReady(keyAction,
                keyAction.Interrupts.Count > 0
                ? keyAction.CanBeInterrupted
                : bits.Target_Alive))
            {
                break;
            }
        }
    }

}
