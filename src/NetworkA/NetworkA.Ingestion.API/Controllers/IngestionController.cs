using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NetworkA.Ingestion.API.Interfaces;
using Shared.Contracts.Payloads;

namespace NetworkA.Ingestion.API.Controllers;

[ApiController]
[Route("api/v1/ingestion")]
public class IngestionController : ControllerBase
{
    private readonly IIngestionService _ingestionService;
    private readonly IValidator<IngestionRequestPayload> _validator;

    public IngestionController(IIngestionService ingestionService, IValidator<IngestionRequestPayload> validator)
    {
        _ingestionService = ingestionService;
        _validator = validator;
    }

    [HttpPost]
    public async Task<IActionResult> IngestAsync([FromBody] IngestionRequestPayload request, CancellationToken ct)
    {
        var validationResult = await _validator.ValidateAsync(request, ct);
        if (!validationResult.IsValid)
        {
            var errors = validationResult.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(
                    g => string.IsNullOrEmpty(g.Key) ? g.Key : char.ToLowerInvariant(g.Key[0]) + g.Key[1..],
                    g => g.Select(e => e.ErrorMessage).ToArray());
            return BadRequest(new { errors });
        }

        var jobId = await _ingestionService.StartJobAsync(request, ct);
        return Accepted(new { jobId });
    }
}
