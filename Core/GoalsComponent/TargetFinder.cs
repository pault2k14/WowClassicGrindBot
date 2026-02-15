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
    private DateTime targetFinderDisabledUntilUtc;

    private DateTime lastActive;
    private static int DISABLE_DUE_TO_BLACKLIST_SECONDS = 12;
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
        IBlacklist targetBlacklist)
    {
        this.input = input;
        this.bits = bits;
        this.npcNameTargeting = npcNameTargeting;
        this.wait = wait;
        this.chatReader = chatReader;
        this.navigation = navigation;
        this.targetBlacklist = targetBlacklist;

        lastActive = DateTime.UtcNow;
        this.chatReader = chatReader;
    }

    private bool IsTargetFinderDisabled()
    => DateTime.UtcNow < targetFinderDisabledUntilUtc;

    private void DisableTargetFinderForBlacklist()
    {
        Console.WriteLine("DisablingTargetFinderForBlacklist: Disabling for " + DISABLE_DUE_TO_BLACKLIST_SECONDS + " seconds!");
        var until = DateTime.UtcNow.Add(DisableDueToBlacklistTimeSpan);
        if (until > targetFinderDisabledUntilUtc)
            targetFinderDisabledUntilUtc = until; // extend, don’t shorten
    }

    public void Reset()
    {
        npcNameTargeting.ChangeNpcType(NpcNames.None);
        npcNameTargeting.Reset();
    }

    public bool Search(
        NpcNames target, Func<bool> validTarget, CancellationToken token)
    {
        // If Assist has requested return we shouldn't be actively looking for
        // a target, but rather than kill the looking for target thread, we
        // simply return false.
        if(chatReader.AssistRequestReturn || navigation.IsInBlacklistArea() || IsTargetFinderDisabled())
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
                    return false;
                }

                return acquiredNonBlacklisted;
            }
        }

        return bits.Target();
    }

}
