namespace Core.Party;

/// <summary>
/// Configuration for the leader/assist HTTP API.
/// Bound from the <c>"PartyApi"</c> section in <c>appsettings.json</c>.
/// </summary>
public sealed class PartyApiConfig
{
    public const string Position = "PartyApi";

    /// <summary>
    /// Base URL of the leader bot's Blazor server.
    /// Each assist bot must point at the leader's address.
    /// Example: <c>http://192.168.1.10:5000</c>
    /// </summary>
    public string LeaderBaseUrl { get; set; } = "http://localhost:5000";

    /// <summary>
    /// Minimum milliseconds between assist→leader state POSTs.
    /// Prevents flooding the leader with updates on every GOAP tick.
    /// Default 500 ms ≈ 2 updates/second.
    /// </summary>
    public int AssistPostIntervalMs { get; set; } = 500;

    /// <summary>
    /// Minimum milliseconds between assist polls of the leader state endpoint.
    /// Default 250 ms ≈ 4 polls/second — fast enough for smooth position tracking.
    /// </summary>
    public int LeaderPollIntervalMs { get; set; } = 250;

    /// <summary>
    /// Unique identifier for this assist bot, used as the key in
    /// <see cref="AssistStateStore"/>. Must differ between bots if multiple
    /// assists are running against the same leader.
    /// </summary>
    public string AssistId { get; set; } = "assist1";

    /// <summary>
    /// Maximum age in milliseconds before a cached <see cref="LeaderState"/> is
    /// considered stale. If the last successful poll is older than this the assist
    /// treats the leader as unreachable and falls back to chat-based recovery.
    /// Default 3000 ms.
    /// </summary>
    public int StaleThresholdMs { get; set; } = 3000;
}
