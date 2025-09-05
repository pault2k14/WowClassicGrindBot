using Core.GOAP;

using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace Core.Goals;

public sealed class CrowdControlGoal : GoapGoal
{
    private readonly ILogger<CrowdControlGoal> logger;
    private readonly ConfigurableInput input;
    private readonly ClassConfiguration classConfig;
    private readonly Wait wait;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly StopMoving stopMoving;
    private readonly CastingHandler castingHandler;
    private readonly IMountHandler mountHandler;
    private readonly CombatLog combatLog;

    public CrowdControlGoal(ILogger<CrowdControlGoal> logger,
        ConfigurableInput input,
        ClassConfiguration classConfig,
        Wait wait,
        PlayerReader playerReader,
        AddonBits bits,
        StopMoving stopMoving,
        CastingHandler castingHandler,
        IMountHandler mountHandler,
        CombatLog combatLog
        )
        : base(nameof(CrowdControlGoal))
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

        this.Keys = classConfig.CrowdControl.Sequence;
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
        input.PressClearTarget();
        wait.Update();
    }

    public override void OnExit()
    {
        wait.Update();
        input.PressClearTarget();
        wait.Update();
    }

    public override void Update()
    {
        Dictionary<int, int> unitGuidDictonary = new Dictionary<int, int>();

        wait.Update();

        /* Tab through all nearby hostile units recording their GUID
         * if we find a mob with a raid icon add it to list of targets  
         */
        
        for(int x = 0; x < 10; x++)
        {
            input.PressNearestTarget();
            wait.Update();

            if(unitGuidDictonary.ContainsKey(playerReader.TargetGuid))
            {
                break;
            }

            unitGuidDictonary.Add(playerReader.TargetGuid, playerReader.TargetRaidIcon());

            for (int i = 0; bits.Target_Alive() && i < Keys.Length; i++)
            {
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
                    wait.Update();
                    break;
                }
            }
        }
    }

}
