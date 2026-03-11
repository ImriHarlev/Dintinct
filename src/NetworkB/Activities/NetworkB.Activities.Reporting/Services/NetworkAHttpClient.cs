using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using NetworkB.Activities.Reporting.Interfaces;
using Shared.Contracts.Payloads;
using Shared.Infrastructure.Options;

namespace NetworkB.Activities.Reporting.Services;

public class NetworkAHttpClient : INetworkAClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NetworkACallbackOptions _options;

    public NetworkAHttpClient(IHttpClientFactory httpClientFactory, IOptions<NetworkACallbackOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public async Task SendFinalStatusAsync(StatusCallbackPayload payload, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient("NetworkA");
        var response = await client.PostAsJsonAsync(
            $"{_options.CallbackBaseUrl}/api/v1/callbacks/status",
            payload,
            ct);

        response.EnsureSuccessStatusCode();
    }

    public async Task SendRetryRequestAsync(string origJobId, string chunkName, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient("NetworkA");
        var response = await client.PostAsJsonAsync(
            $"{_options.CallbackBaseUrl}/api/v1/callbacks/retry",
            new { origJobId, chunkName },
            ct);

        response.EnsureSuccessStatusCode();
    }
}
