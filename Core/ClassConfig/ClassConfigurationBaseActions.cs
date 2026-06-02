using Game;

namespace Core;

public sealed partial class ClassConfiguration
{
    public KeyAction Jump { get; } = new()
    {
        Name = nameof(Jump),
        Key = "Spacebar",
        BaseAction = true
    };

    public KeyAction Interact { get; } = new()
    {
        Key = "I",
        Name = nameof(Interact),
        Cooldown = 0,
        PressDuration = InputDuration.FastPress,
        BaseAction = true
    };

    public KeyAction InteractMouseOver { get; } = new()
    {
        Key = "J",
        Name = nameof(InteractMouseOver),
        Cooldown = 0,
        PressDuration = InputDuration.VeryFastPress,
        BaseAction = true
    };

    public KeyAction Approach { get; } = new()
    {
        Key = "I", // Interact.Key
        Name = nameof(Approach),
        PressDuration = 10,
        BaseAction = true,
        Requirement = "!SoftTargetDead"
    };

    public KeyAction AutoAttack { get; } = new()
    {
        Key = "I", // Interact.Key
        Name = nameof(AutoAttack),
        BaseAction = true,
        Requirement = "!AutoAttacking && !SoftTargetDead"
    };

    public KeyAction TargetLastTarget { get; } = new()
    {
        Key = "G",
        Name = nameof(TargetLastTarget),
        Cooldown = 0,
        BaseAction = true
    };

    public KeyAction StandUp { get; } = new()
    {
        Key = "X",
        Name = nameof(StandUp),
        Cooldown = 0,
        BaseAction = true,
    };

    public KeyAction ClearTarget { get; } = new()
    {
        Key = "Insert",
        Name = nameof(ClearTarget),
        Cooldown = 0,
        BaseAction = true,
    };

    public KeyAction StopAttack { get; } = new()
    {
        Key = "Delete",
        Name = nameof(StopAttack),
        PressDuration = InputDuration.FastPress,
        BaseAction = true,
    };

    public KeyAction TargetNearestTarget { get; } = new()
    {
        Key = "Tab",
        Name = nameof(TargetNearestTarget),
        BaseAction = true,
        PressDuration = InputDuration.FastPress
    };

    public KeyAction TargetTargetOfTarget { get; } = new()
    {
        Key = "F",
        Name = nameof(TargetTargetOfTarget),
        Cooldown = 0,
        BaseAction = true,
    };

    public KeyAction TargetPet { get; } = new()
    {
        Key = "Multiply",
        Name = nameof(TargetPet),
        Cooldown = 0,
        BaseAction = true,
    };

    public KeyAction PetAttack { get; } = new()
    {
        Key = "Subtract",
        Name = nameof(PetAttack),
        PressDuration = InputDuration.VeryFastPress,
        BaseAction = true,
    };

    public KeyAction TargetFocus { get; } = new()
    {
        Key = "PageUp",
        Name = nameof(TargetFocus),
        Cooldown = 0,
        BaseAction = true,
    };

    public KeyAction TargetFocusPartyMemberTwo { get; } = new()
    {
        Key = "Oemcomma",
        Name = nameof(TargetFocusPartyMemberTwo),
        Cooldown = 0,
        BaseAction = true,
    };

    public KeyAction TargetFocusPartyMemberThree { get; } = new()
    {
        Key = "OemPeriod",
        Name = nameof(TargetFocusPartyMemberThree),
        Cooldown = 0,
        BaseAction = true,
    };

    public KeyAction TargetFocusPartyMemberFour { get; } = new()
    {
        Key = "Oem2",
        Name = nameof(TargetFocusPartyMemberFour),
        Cooldown = 0,
        BaseAction = true,
    };

    public KeyAction FollowTarget { get; } = new()
    {
        Key = "PageDown",
        Name = nameof(FollowTarget),
        Cooldown = 0,
        BaseAction = true,
    };

    public KeyAction AssistIsFollowing { get; } = new()
    {
        Key = "N3",
        Name = nameof(AssistIsFollowing),
        Cooldown = 0,
        BaseAction = true,
    };

    public KeyAction AssistIsNotFollowing { get; } = new()
    {
        Key = "N4",
        Name = nameof(AssistIsNotFollowing),
        Cooldown = 0,
        BaseAction = true,
    };

    public KeyAction AssistCantFollow { get; } = new()
    {
        Key = "N5",
        Name = nameof(AssistCantFollow),
        Cooldown = 0,
        BaseAction = true,
    };

    public KeyAction EnableSoftInteract { get; } = new()
    {
        Key = "N6",
        Name = nameof(EnableSoftInteract),
        Cooldown = 0,
        BaseAction = true,
    };

    public KeyAction DisableSoftInteract { get; } = new()
    {
        Key = "N7",
        Name = nameof(DisableSoftInteract),
        Cooldown = 0,
        BaseAction = true,
    };

    /// <summary>
    /// Assist presses this to ask the leader "leader what is your position?"
    /// The in-game macro sends that text to party chat.
    /// </summary>
    public KeyAction AssistRequestLeaderPosition { get; } = new()
    {
        Key = "N8",
        Name = nameof(AssistRequestLeaderPosition),
        Cooldown = 0,
        BaseAction = true,
    };

    /// <summary>
    /// Leader presses this to reply "position: x,y" to party chat.
    /// The in-game macro substitutes the leader's actual map coordinates.
    /// </summary>
    public KeyAction LeaderReplyPosition { get; } = new()
    {
        Key = "N9",
        Name = nameof(LeaderReplyPosition),
        Cooldown = 0,
        BaseAction = true,
    };

    public KeyAction Mount { get; } = new()
    {
        Key = "O",
        Name = nameof(Mount),
        BaseAction = true,
        Cooldown = 6000,
    };
}
