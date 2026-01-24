using Core.GOAP;

using Microsoft.Extensions.Logging;

using System.Threading.Tasks;

namespace Core.Goals;

public sealed class ParallelGoal : GoapGoal
{
    public override float Cost => 3f;

    private readonly ILogger logger;
    private readonly ConfigurableInput input;
    private readonly StopMoving stopMoving;
    private readonly Wait wait;
    private readonly PlayerReader playerReader;
    private readonly CastingHandler castingHandler;
    private readonly IMountHandler mountHandler;
    private readonly RestHandler restHandler;
    private readonly ChatReader chatReader;

    private static bool None() => false;

    private bool castSuccess;

    public ParallelGoal(ILogger logger, ConfigurableInput input, Wait wait,
        PlayerReader playerReader, StopMoving stopMoving, ClassConfiguration classConfig,
        CastingHandler castingHandler, IMountHandler mountHandler, 
        RestHandler restHandler, ChatReader chatReader)
        : base(nameof(ParallelGoal))
    {
        this.logger = logger;
        this.input = input;
        this.stopMoving = stopMoving;
        this.wait = wait;
        this.playerReader = playerReader;
        this.castingHandler = castingHandler;
        this.mountHandler = mountHandler;
        this.chatReader = chatReader;

        AddPrecondition(GoapKey.forcedfollow, false);
        AddPrecondition(GoapKey.incombat, false);
        AddPrecondition(GoapKey.assistrequestreturn, false);

        Keys = classConfig.Parallel.Sequence;
        this.restHandler = restHandler;
    }

    public override bool CanRun()
    {
        for (int i = 0; i < Keys.Length; i++)
        {
            if (Keys[i].CanRun())
                return true;
        }
        return false;
    }

    public override void OnEnter()
    {
        if (mountHandler.IsMounted())
        {
            mountHandler.Dismount();
        }

        while (restHandler.IsResting())
        {
            wait.Update(1000);
        }

        for (int i = 0; i < Keys.Length; i++)
        {
            if (Keys[i].BeforeCastStop)
            {
                stopMoving.Stop();
                wait.Update();
                break;
            }
        }
    }

    public override void Update()
    {
        if (chatReader.ForcedFollow)
        {
            AddEffect(GoapKey.forcedfollow, true);
            return;
        }

        if (castingHandler.SpellInQueue())
        {
            wait.Update();
            return;
        }

        if (!castSuccess)
        {
            Cast();
            
            wait.Update(playerReader.DoubleNetworkLatency);
            wait.Update();
        }
    }

    public override void OnExit()
    {
        castSuccess = false;
        wait.Update();
    }

    private void Cast()
    {
        Parallel.For(0, Keys.Length, Execute);
    }

    private void Execute(int i)
    {
        if (castingHandler.CastIfReady(Keys[i], None))
        {
            Keys[i].ResetCooldown();
            Keys[i].SetClicked();

            castSuccess = true;
        }
    }
}