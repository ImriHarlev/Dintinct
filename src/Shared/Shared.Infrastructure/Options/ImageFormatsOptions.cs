namespace Shared.Infrastructure.Options;

public class ImageFormatsOptions
{
    public List<string> SupportedExtensions { get; set; } = [];

    public bool IsSupported(string extension) =>
        SupportedExtensions.Any(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase));
}
