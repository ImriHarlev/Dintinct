using Shared.Contracts.Enums;

namespace Shared.Contracts.Models;

public record FileResult(
    FileTransferStatus Status,
    string DirPath);
