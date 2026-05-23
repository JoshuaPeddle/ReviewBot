using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using ReviewBot.Grounding.Detection;

namespace ReviewBot.Grounding.Languages.DotNet;

public sealed class DotNetLanguageDetector : ILanguageDetector
{
    private static readonly string[] Extensions = [".csproj", ".sln", ".slnx"];

    private readonly ILogger<DotNetLanguageDetector> _logger;

    public DotNetLanguageDetector(ILogger<DotNetLanguageDetector> logger) => _logger = logger;

    public string LanguageId => "dotnet";

    public bool CanDetect(IReadOnlyList<string> rootFileNames) =>
        rootFileNames.Any(f =>
            Extensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) ||
            f.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase));

    public async Task<LanguageMetadata?> ExtractMetadataAsync(
        IRepoContentReader reader, string headSha, CancellationToken ct)
    {
        // 1. Try Directory.Build.props first
        var xmlContent = await reader.TryReadFileAsync("Directory.Build.props", headSha, ct).ConfigureAwait(false);

        // 2. Fall back to first .csproj in root
        if (xmlContent is null)
        {
            var rootFiles = await reader.ListRootFilesAsync(headSha, ct).ConfigureAwait(false);
            var csproj = rootFiles.FirstOrDefault(f =>
                f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
            if (csproj is not null)
                xmlContent = await reader.TryReadFileAsync(csproj, headSha, ct).ConfigureAwait(false);
        }

        if (xmlContent is null)
            return null;

        if (!TryParseProjectXml(xmlContent, out var tfm, out var langVersion, out var nullable,
                out var treatWarningsAsErrors, out var implicitUsings))
            return null;

        if (tfm is null)
            return null;

        // 3. Try global.json for SDK version
        string? toolchainVersion = null;
        var globalJson = await reader.TryReadFileAsync("global.json", headSha, ct).ConfigureAwait(false);
        if (globalJson is not null)
            toolchainVersion = ParseGlobalJsonSdkVersion(globalJson);

        return new LanguageMetadata(
            LanguageId,
            MapTfmToVersion(tfm),
            toolchainVersion,
            BuildFacts(langVersion, nullable, treatWarningsAsErrors, implicitUsings));
    }

    private bool TryParseProjectXml(
        string xml,
        out string? tfm,
        out string? langVersion,
        out string? nullable,
        out string? treatWarningsAsErrors,
        out string? implicitUsings)
    {
        tfm = langVersion = nullable = treatWarningsAsErrors = implicitUsings = null;
        try
        {
            var doc = XDocument.Parse(xml);
            tfm = FindElement(doc, "TargetFramework");
            langVersion = FindElement(doc, "LangVersion");
            nullable = FindElement(doc, "Nullable");
            treatWarningsAsErrors = FindElement(doc, "TreatWarningsAsErrors");
            implicitUsings = FindElement(doc, "ImplicitUsings");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse MSBuild XML for .NET grounding");
            return false;
        }
    }

    private static string? FindElement(XDocument doc, string name) =>
        doc.Descendants(name).FirstOrDefault()?.Value?.Trim();

    private static string? ParseGlobalJsonSdkVersion(string json)
    {
        var match = Regex.Match(
            json,
            @"""sdk""\s*:\s*\{[^}]*""version""\s*:\s*""([^""]+)""",
            RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string MapTfmToVersion(string tfm)
    {
        // "net10.0" -> "10.0", "net10.0-windows" -> "10.0"
        if (!tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            return tfm;

        var rest = tfm[3..];
        var dash = rest.IndexOf('-');
        return dash > 0 ? rest[..dash] : rest;
    }

    private static IReadOnlyList<string> BuildFacts(
        string? langVersion,
        string? nullable,
        string? treatWarningsAsErrors,
        string? implicitUsings)
    {
        var facts = new List<string>(4);
        if (!string.IsNullOrEmpty(langVersion))
            facts.Add($"LangVersion: {langVersion}");
        if (!string.IsNullOrEmpty(nullable))
            facts.Add($"Nullable: {nullable}");
        if (!string.IsNullOrEmpty(treatWarningsAsErrors))
            facts.Add($"TreatWarningsAsErrors: {treatWarningsAsErrors}");
        if (!string.IsNullOrEmpty(implicitUsings))
            facts.Add($"ImplicitUsings: {implicitUsings}");
        return facts.AsReadOnly();
    }
}
