using Core.GOAP;

using Microsoft.Extensions.Logging;

using SharedLib.Extensions;

using System;
using System.Numerics;

using static System.MathF;

namespace Core.Goals;

public sealed class WrongZoneGoal : GoapGoal
{
    public override float Cost => 19f;

    private readonly ILogger<WrongZoneGoal> logger;
    private readonly ConfigurableInput input;
    private readonly PlayerReader playerReader;
    private readonly PlayerDirection playerDirection;
    private readonly StuckDetector stuckDetector;
    private readonly ClassConfiguration classConfig;

    private float lastDistance = 999;

    public DateTime LastActive { get; private set; }

    public WrongZoneGoal(ILogger<WrongZoneGoal> logger,
        PlayerReader playerReader, ConfigurableInput input,
        PlayerDirection playerDirection,
        StuckDetector stuckDetector, ClassConfiguration classConfig)
        : base(nameof(WrongZoneGoal))
    {
        this.playerReader = playerReader;
        this.input = input;
        this.playerDirection = playerDirection;
        this.logger = logger;
        this.stuckDetector = stuckDetector;
        this.classConfig = classConfig;

        AddPrecondition(GoapKey.incombat, false);
    }

    public override bool CanRun()
    {
        return playerReader.UIMapId.Value == classConfig.WrongZone.ZoneId;
    }

    public override void Update()
    {
        Vector3 exitMap = classConfig.WrongZone.ExitZoneLocation;

        input.StartForward(true);

        if ((DateTime.UtcNow - LastActive).TotalMilliseconds > 10000)
        {
            stuckDetector.SetTargetLocation(exitMap);
        }

        Vector3 playerMap = playerReader.MapPos;
        float mapDistance = playerMap.MapDistanceXYTo(exitMap);
        float heading = DirectionCalculator.CalculateMapHeading(playerMap, exitMap);

        if (lastDistance < mapDistance)
        {
            logger.LogInformation("Further away");
            playerDirection.SetDirection(heading, exitMap);
        }
        else if (!stuckDetector.IsGettingCloser())
        {
            input.StartForward(true);

            if (HasBeenActiveRecently())
            {
                stuckDetector.Update();
            }
            else
            {
                logger.LogInformation("Resuming movement");
            }
        }
        else
        {
            float diff1 = Abs(Tau + heading - playerReader.Direction) % Tau;
            float diff2 = Abs(heading - playerReader.Direction - Tau) % Tau;

            if (Min(diff1, diff2) > 0.3)
            {
                logger.LogInformation("Correcting direction");
                playerDirection.SetDirection(heading, exitMap);
            }
        }

        lastDistance = mapDistance;

        LastActive = DateTime.UtcNow;
    }

    // ── Fix CA (audit finding from run-155 follow-up) ──
    //
    // WrongZoneGoal.Update calls `input.StartForward(true)` unconditionally
    // at line ~54 and conditionally at line ~72. Without this override the
    // class inherits the empty `GoapGoal.OnExit() { }`. When CanRun() returns
    // false (bot has left the configured wrong zone), GoapAgent fires the
    // empty default OnExit and the Forward key stays held until something
    // else releases it.
    //
    // Fix BR's defensive release in Navigation.Stop() does NOT cover this
    // path because WrongZoneGoal doesn't use Navigation — it drives stuck
    // detection through stuckDetector.SetTargetLocation directly. So no
    // Stop() ever fires, and Forward leaks past the goal boundary.
    //
    // Result before this fix: bot exits wrong zone but keeps running in
    // its last facing direction until the next goal's Navigation eventually
    // calls Stop(), or until shutdown. Same bug class as run-155 forward-key
    // runaway, just triggered by a different upstream goal.
    //
    // Idempotency: input.StopForward only sends the release if the key is
    // currently down (ConfigurableInput.cs:50 IsKeyDown guard), so OnExit
    // calls on cycles where Forward wasn't pressed are no-ops.
    public override void OnExit()
    {
        input.StopForward(false);
    }

    private bool HasBeenActiveRecently()
    {
        return (DateTime.UtcNow - LastActive).TotalMilliseconds < 2000;
    }
}