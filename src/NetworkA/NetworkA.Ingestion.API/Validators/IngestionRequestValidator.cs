using FluentValidation;
using Shared.Contracts.Enums;
using Shared.Contracts.Payloads;

namespace NetworkA.Ingestion.API.Validators;

public class IngestionRequestValidator : AbstractValidator<IngestionRequestPayload>
{
    public IngestionRequestValidator()
    {
        RuleFor(x => x.CallingSystemId).NotEmpty();
        RuleFor(x => x.CallingSystemName).NotEmpty();
        RuleFor(x => x.SourcePath).NotEmpty();
        RuleFor(x => x.TargetPath).NotEmpty();
        RuleFor(x => x.TargetNetwork).NotEmpty();
        RuleFor(x => x.ExternalId).NotEmpty();
        RuleFor(x => x.AnswerType).IsInEnum();
        RuleFor(x => x.AnswerLocation)
            .NotEmpty()
            .WithMessage("'Answer Location' is required when AnswerType is FileSystem.")
            .Must(p => string.IsNullOrEmpty(Path.GetExtension(p)))
            .WithMessage("'Answer Location' must be a directory path, not a file path.")
            .When(x => x.AnswerType == AnswerType.FileSystem);
    }
}
