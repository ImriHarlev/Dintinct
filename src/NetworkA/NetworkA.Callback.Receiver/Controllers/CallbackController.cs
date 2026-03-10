using Microsoft.AspNetCore.Mvc;
using Shared.Contracts.Interfaces;
using Shared.Contracts.Payloads;

namespace NetworkA.Callback.Receiver.Controllers;

[ApiController]
[Route("api/v1/callbacks")]
public class CallbackController : ControllerBase
{
    private readonly ICallbackService _callbackService;
    private readonly ILogger<CallbackController> _logger;

    public CallbackController(ICallbackService callbackService, ILogger<CallbackController> logger)
    {
        _callbackService = callbackService;
        _logger = logger;
    }

    [HttpPost("status")]
    public async Task<IActionResult> ReceiveFinalStatus(
        [FromBody] StatusCallbackPayload payload,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payload.OrigJobId))
            return BadRequest(new { error = "OrigJobId is required" });

        await _callbackService.HandleFinalStatusAsync(payload, ct);
        return NoContent();
    }

    [HttpPost("retry")]
    public async Task<IActionResult> RequestChunkRetry(
        [FromBody] ChunkRetryRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.OrigJobId))
            return BadRequest(new { error = "OrigJobId is required" });

        if (string.IsNullOrWhiteSpace(request.ChunkName))
            return BadRequest(new { error = "ChunkName is required" });

        await _callbackService.HandleChunkRetryRequestAsync(request.OrigJobId, request.ChunkName, ct);
        return NoContent();
    }
}

public record ChunkRetryRequest(string OrigJobId, string ChunkName);
