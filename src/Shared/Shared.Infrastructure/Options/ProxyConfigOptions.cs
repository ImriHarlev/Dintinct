using Shared.Contracts.Models;

namespace Shared.Infrastructure.Options;

public class ProxyConfigOptions
{
    public List<ProxyConfiguration> Configurations { get; set; } = [];

    /// <summary>
    /// Returns true when <paramref name="sourceFormat"/> has <paramref name="targetFormat"/>
    /// anywhere in its RequiredConversion list (including multi-target entries).
    /// </summary>
    public bool HasConversion(string sourceFormat, string targetFormat) =>
        Configurations.Any(c =>
            c.SourceFormat.Equals(sourceFormat, StringComparison.OrdinalIgnoreCase) &&
            c.RequiredConversion.Any(r => r.Equals(targetFormat, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Returns true when <paramref name="sourceFormat"/> converts exclusively to
    /// <paramref name="targetFormat"/> — i.e. RequiredConversion has exactly one element
    /// matching <paramref name="targetFormat"/>. This distinguishes plain image formats
    /// (RequiredConversion: ["PNG"]) from formats that also convert to other targets
    /// (e.g. pptx: ["DOCX", "PNG"]).
    /// </summary>
    public bool IsExclusiveConversion(string sourceFormat, string targetFormat) =>
        Configurations.Any(c =>
            c.SourceFormat.Equals(sourceFormat, StringComparison.OrdinalIgnoreCase) &&
            c.RequiredConversion.Count == 1 &&
            c.RequiredConversion[0].Equals(targetFormat, StringComparison.OrdinalIgnoreCase));
}
