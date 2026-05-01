using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Party;

/// <summary>
/// Assist-side HTTP client implementation of <see cref="IPartyApiClient"/>.
/// Uses a single long-lived <see cref="HttpClient"/> with a short timeout so a
/// slow or absent leader never blocks the GOAP tick loop.
/// <para>
/// All exceptions are caught and logged at Debug level — the caller always gets
/// either a result or <c>null</c> / a completed task, never an exception.
/// </para>
/// </summary>
public sealed class PartyApiClient : IPartyApiClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PartyApiClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public PartyApiClient(
        ILogger<PartyApiClient> logger,
        IOptions<PartyApiConfig> configOptions)
    {
        _logger = logger;

        PartyApiConfig config = configOptions.Value;

        _httpClient = new HttpClient
        {
            // Ensure the base address ends with '/' so relative paths compose correctly.
            BaseAddress = new Uri(config.LeaderBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(2)
        };

        // Case-insensitive deserialization + string enum names matching the server.
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    /// <inheritdoc/>
    public async Task<LeaderState?> GetLeaderStateAsync(CancellationToken ct = default)
    {
        try
        {
            using HttpResponseMessage response =
                await _httpClient.GetAsync("party/leader/state", ct)
                    .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            return await response.Content
                .ReadFromJsonAsync<LeaderState>(_jsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // propagate cancellation — caller manages the token
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[PartyApiClient] GetLeaderStateAsync failed — leader may be offline.");
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task PostAssistStateAsync(AssistState state, CancellationToken ct = default)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient
                .PostAsJsonAsync("party/assist/state", state, _jsonOptions, ct)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException)
        {
            // Normal during shutdown — don't log as an error.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[PartyApiClient] PostAssistStateAsync failed.");
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
