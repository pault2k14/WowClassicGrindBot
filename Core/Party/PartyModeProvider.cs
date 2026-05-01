namespace Core.Party;

/// <summary>
/// Singleton that holds the bot's current <see cref="Mode"/> once a class profile
/// is loaded. Populated by <see cref="BotController"/> in <c>InitialiseFromFile</c>
/// alongside the existing <c>chatReader.BotMode = ClassConfig.Mode</c> assignment.
/// <para>
/// Needed because <see cref="PartyStatePublisher"/> is a singleton <see cref="IReader"/>
/// (constructed before any profile loads) but must behave differently based on whether
/// the bot is a leader or assist. <see cref="ClassConfiguration"/> is session-scoped
/// and cannot be injected into a singleton, so the mode is hoisted into this
/// long-lived wrapper instead.
/// </para>
/// </summary>
public sealed class PartyModeProvider
{
    /// <summary>
    /// The current bot mode. <c>null</c> until a profile is loaded.
    /// Set by <see cref="BotController"/> immediately after setting
    /// <see cref="ChatReader.BotMode"/>.
    /// </summary>
    public Mode? BotMode { get; set; }
}
