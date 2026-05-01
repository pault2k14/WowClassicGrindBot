using Core.Party;

using Microsoft.AspNetCore.Mvc;

using System;

namespace BlazorServer.Controllers;

/// <summary>
/// Exposes the leader/assist party-coordination API.
/// <list type="bullet">
///   <item><c>GET  /party/leader/state</c> — assist polls for the leader's live position and status.</item>
///   <item><c>POST /party/assist/state</c> — assist pushes its own state to the leader.</item>
/// </list>
/// Rate limiting is performed on the client side via <see cref="PartyApiConfig"/>.
/// The endpoints themselves are stateless read/writes against singleton services.
/// </summary>
[ApiController]
[Route("party")]
public sealed class PartyController : ControllerBase
{
    /// <summary>
    /// Returns a fresh snapshot of the leader bot's current position, status, and health.
    /// Called by each assist at ~4 Hz (configurable via <see cref="PartyApiConfig.LeaderPollIntervalMs"/>).
    /// The snapshot is built on-demand from the live <see cref="Core.PlayerReader"/> — no caching.
    /// </summary>
    [HttpGet("leader/state")]
    public IActionResult GetLeaderState([FromServices] LeaderStateService leaderStateService)
    {
        return Ok(leaderStateService.GetCurrentState());
    }

    /// <summary>
    /// Stores the latest state snapshot for one assist bot.
    /// The leader's goals read from <see cref="AssistStateStore"/> in later steps instead
    /// of parsing chat messages for "i'm following" / "i tried following…".
    /// </summary>
    [HttpPost("assist/state")]
    public IActionResult PostAssistState(
        [FromServices] AssistStateStore store,
        [FromBody] AssistState state)
    {
        if (string.IsNullOrWhiteSpace(state.AssistId))
            return BadRequest("AssistId is required.");

        // Overwrite with an authoritative server-side timestamp so the leader's
        // AgeMs calculation is not distorted by clock skew between the two machines.
        state.Timestamp = DateTime.UtcNow;
        store.Update(state);
        return Ok();
    }
}
