namespace Proxy.MockService.Options;

public class SimulationOptions
{
    /// <summary>
    /// Percentage of files that will be simulated as transfer errors (0–100).
    /// When triggered, a .ERROR.txt file is written to the outbox instead of the real file.
    /// </summary>
    public int ErrorRatePercent { get; set; } = 0;

    /// <summary>
    /// File extensions (e.g. ".exe", ".bat") that will always be reported as unsupported.
    /// A .UNSUPPORTED.txt file is written to the outbox instead of the real file.
    /// </summary>
    public string[] UnsupportedExtensions { get; set; } = [];
}
