using Core.Party;

using Microsoft.Extensions.Logging;

using SharedLib.NpcFinder;

using System;
using System.Threading;

namespace Core.Goals;

public sealed class TargetFinder : IDisposable
{
    private const int waitMs = 200;
    private bool _disposed;

    private readonly ConfigurableInput input;
    private readonly AddonBits bits;
    private readonly NpcNameTargeting npcNameTargeting;
    private readonly Wait wait;
    private readonly ChatReader chatReader;
    private readonly Navigation navigation;
    private readonly IBlacklist targetBlacklist;
    private readonly AssistStateStore assistStateStore;
    private readonly ClassConfiguration classConfig;
    private readonly AssistStatusProvider assistStatusProvider;

    private DateTime targetFinderDisabledUntilUtc;

    private DateTime lastActive;
    private static int DISABLE_DUE_TO_BLACKLIST_SECONDS = 6;
    private TimeSpan DisableDueToBlacklistTimeSpan = new TimeSpan(0, 0, 0, DISABLE_DUE_TO_BLACKLIST_SECONDS);
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        npcNameTargeting.Dispose();
    }

    public int ElapsedMs =>
        (int)(DateTime.UtcNow - lastActive).TotalMilliseconds;

    public TargetFinder(ConfigurableInput input,
        AddonBits bits, NpcNameTargeting npcNameTargeting, 
        Wait wait, ChatReader chatReader, Navigation navigation,
        IBlacklist targetBlacklist,
        AssistStateStore assistStateStore,
        ClassConfiguration classConfig,
        AssistStatusProvider assistStatusProvider)
    {
        this.input = input;
        this.bits = bits;
        this.npcNameTargeting = npcNameTargeting;
        this.wait = wait;
        this.chatReader = chatReader;
        this.navigation = navigation;
        this.targetBlacklist = targetBlacklist;
        this.assistStateStore = assistStateStore;
        this.classConfig = classConfig;
        this.assistStatusProvider = assistStatusProvider;

        lastActive = DateTime.UtcNow;
    }

    private bool IsTargetFinderDisabled()
    => DateTime.UtcNow < targetFinderDisabledUntilUtc;

    private void DisableTargetFinderForBlacklist()
    {
        Console.WriteLine("DisablingTargetFinderForBlacklist: Disabling for " + DISABLE_DUE_TO_BLACKLIST_SECONDS + " seconds!");
        var until = DateTime.UtcNow.Add(DisableDueToBlacklistTimeSpan);
        if (until > targetFinderDisabledUntilUtc)
            targetFinderDisabledUntilUtc = until; // extend, don't shorten
    }

    /// <summary>
    /// Extend the internal "disabled" window from an external caller. Used by
    /// <c>FollowRouteGoal.SuppressTargetFinderBriefly</c> to gate
    /// <see cref="Search"/> for the full 25 s evade-recovery window — without
    /// this the side thread's <c>Thread_LookingForTarget</c> can press Tab
    /// inside the suppression window because <c>ManualResetEventSlim.Reset()</c>
    /// only blocks future <c>Wait()</c> calls, not threads already past Wait
    /// and inside <see cref="Search"/>.
    ///
    /// Observed in log 25 (leader 23:01:23:458 SuppressTargetFinderBriefly →
    /// 23:01:23:731 Tab pressed by side thread, 273 ms later → "Found target!"
    /// re-acquired the blacklisted mob 7963945 → wantNavPaused=true → leader
    /// stationary for the rest of the 25 s window).
    ///
    /// Same "extend, don't shorten" policy as
    /// <see cref="DisableTargetFinderForBlacklist"/>.
    /// </summary>
    public void DisableUntil(DateTime utc)
    {
        if (utc > targetFinderDisabledUntilUtc)
            targetFinderDisabledUntilUtc = utc;
    }

    public void Reset()
    {
        npcNameTargeting.ChangeNpcType(NpcNames.None);
        npcNameTargeting.Reset();
    }

    public bool Search(
        NpcNames target, Func<bool> validTarget, CancellationToken token)
    {
        // PartyLeader: stop searching while assist is stuck (leader needs to navigate back).
        // AssistFocus: assistStatusProvider.CantFollow is the assist's own self-reported flag
        // (set on evade/CantFollow, cleared when back in Following range) — replaces
        // chatReader.AssistRequestReturn which was never designed as a state container.
        bool cantReturn = classConfig.Mode == Mode.PartyLeader
            ? assistStateStore.AnyAssistCantFollow()
            : assistStatusProvider.CantFollow;

        if (cantReturn || navigation.IsInBlacklistArea() || IsTargetFinderDisabled())
        {
            return false;
        }
        else
        {
            return LookForTarget(target, token) && validTarget();
        }
    }

    private bool LookForTarget(
        NpcNames target, CancellationToken token)
    {
        if (ElapsedMs < waitMs)
            return bits.Target();

        if (!input.TargetNearestTarget.OnCooldown())
        {
            lastActive = DateTime.UtcNow;
            input.PressNearestTarget(token);
            wait.Update(token);
        }

        if (!token.IsCancellationRequested &&
            !input.KeyboardOnly && !bits.Target())
        {
            npcNameTargeting.ChangeNpcType(target);
            npcNameTargeting.WaitForUpdate(token);

            if (token.IsCancellationRequested)
                return false;

            if (targetBlacklist.Is())
            {
                Console.WriteLine("TargetFinder: Target was Blacklisted");
                DisableTargetFinderForBlacklist();
                return false;
            }

            if (npcNameTargeting.FoundAny() &&
                !input.IsKeyDown(input.TurnLeftKey) &&
                !input.IsKeyDown(input.TurnRightKey))
            {
                lastActive = DateTime.UtcNow;
                bool acquiredNonBlacklisted = npcNameTargeting.AcquireNonBlacklisted(token);

                if(npcNameTargeting.lastTargetWasBlacklisted)
                {
                    Console.WriteLine("TargetFinder: npcNameTargeting.AcquireNonBlacklisted shows last target was blacklisted");
                    DisableTargetFinderForBlacklist();
                    npcNameTargeting.lastTargetWasBlacklisted = false;
                    return false;
                }

                return acquiredNonBlacklisted;
            }
        }

        return bits.Target();
    }
}
