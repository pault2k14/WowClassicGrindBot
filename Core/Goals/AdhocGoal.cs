using Core.GOAP;

using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;

namespace Core.Goals;

public sealed class AdhocGoal : GoapGoal
{
    public override float Cost => key.Cost;

    private readonly ILogger logger;
    private readonly ConfigurableInput input;

    private readonly Wait wait;
    private readonly StopMoving stopMoving;
    private readonly PlayerReader playerReader;

    private readonly KeyAction key;
    private readonly CastingHandler castingHandler;
    private readonly IMountHandler mountHandler;
    private readonly AddonBits bits;
    private readonly CombatLog combatLog;
    private readonly RestHandler restHandler;
    private readonly ChatReader chatReader;

    private readonly bool? combatMatters;

    public AdhocGoal(KeyAction key, ILogger logger,
        ConfigurableInput input, Wait wait,
        PlayerReader playerReader, StopMoving stopMoving,
        CastingHandler castingHandler, IMountHandler mountHandler,
        AddonBits bits, CombatLog combatLog, RestHandler restHandler,
        ChatReader chatReader)
        : base(nameof(AdhocGoal))
    {
        this.logger = logger;
        this.input = input;
        this.wait = wait;
        this.stopMoving = stopMoving;
        this.playerReader = playerReader;
        this.key = key;
        this.castingHandler = castingHandler;
        this.mountHandler = mountHandler;
        this.bits = bits;
        this.combatLog = combatLog;
        this.restHandler = restHandler;
        this.chatReader = chatReader;

        if (bool.TryParse(key.InCombat, out bool result))
        {
            AddPrecondition(GoapKey.incombat, result);
            combatMatters = result;
        }

        AddPrecondition(GoapKey.leaderWaitingForAssist, false);
        //AddPrecondition(GoapKey.forcedfollow, false);
        Keys = [key];
    }

    public override bool CanRun() => key.CanRun();

    public override void OnEnter()
    {
        if (key.BeforeCastDismount && mountHandler.IsMounted())
        {
            mountHandler.Dismount();
        }

        while(restHandler.IsResting())
        {
            wait.Update(1000);
        }
    }

    public override void Update()
    {
        // ── Fix GK (run-169 extension) — AdhocGoal Update-entry wait timing ──
        // See FollowFocusGoal.cs Fix GK block (~line 3326) for full writeup.
        long gkStart = Stopwatch.GetTimestamp();
        wait.Update();
        double gkElapsedMs = Stopwatch.GetElapsedTime(gkStart).TotalMilliseconds;
        if (gkElapsedMs > 5000)
        {
            logger.LogWarning(
                $"[AdhocGoal] [FIX-FIRE] GK: Update-entry wait.Update() blocked for " +
                $"{gkElapsedMs:0}ms (>5000ms threshold). Likely cause: WoW addon " +
                $"GlobalTime counter stalled. State at unblock: " +
                $"TargetGuid={playerReader.TargetGuid} " +
                $"bits.Combat={bits.Combat()}.");
        }
        /*
        if (chatReader.ForcedFollow)
        {
            AddEffect(GoapKey.forcedfollow, true);
            return;
        }
        */

        if (bits.Drowning())
        {
            input.PressJump();
            return;
        }

        if (!CanRun() || castingHandler.SpellInQueue())
            return;

        if (key.Charge >= 1 && key.CanRun())
            if (chatReader.ForcedFollow && !key.UseWithForcedFollow)
            {
                return;
            } 
        castingHandler.CastIfReady(key, Interrupt);
    }

    private bool Interrupt()
    {
        return combatMatters.HasValue
            ? combatMatters.Value == bits.Combat() && combatLog.DamageTakenCount() > 0
            : combatLog.DamageTakenCount() > 0;
    }
}
