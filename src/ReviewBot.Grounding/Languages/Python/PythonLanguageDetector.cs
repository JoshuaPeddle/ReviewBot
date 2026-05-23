using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ReviewBot.Grounding.Detection;

namespace ReviewBot.Grounding.Languages.Python;

public sealed class PythonLanguageDetector : ILanguageDetector
{
    private static readonly string[] SignalFiles =
        ["pyproject.toml", "setup.py", "setup.cfg", "requirements.txt", ".python-version"];

    private readonly ILogger<PythonLanguageDetector> _logger;

    public PythonLanguageDetector(ILogger<PythonLanguageDetector> logger) => _logger = logger;

    public string LanguageId => "python";

    public bool CanDetect(IReadOnlyList<string> rootFileNames) =>
        rootFileNames.Any(f => SignalFiles.Any(s => f.Equals(s, StringComparison.OrdinalIgnoreCase)));

    public async Task<LanguageMetadata?> ExtractMetadataAsync(
        IRepoContentReader reader, string headSha, CancellationToken ct)
    {
        // 1. pyproject.toml (highest priority)
        var pyproject = await reader.TryReadFileAsync("pyproject.toml", headSha, ct).ConfigureAwait(false);
        if (pyproject is not null)
            return ParsePyproject(pyproject);

        // 2. .python-version
        var pythonVersionFile = await reader.TryReadFileAsync(".python-version", headSha, ct).ConfigureAwait(false);
        if (pythonVersionFile is not null)
        {
            var rawVersion = pythonVersionFile.Trim();
            var majorMinor = ExtractMajorMinor(rawVersion);
            return majorMinor is null
                ? null
                : new LanguageMetadata(LanguageId, majorMinor, rawVersion, []);
        }

        // 3. setup.cfg
        var setupCfg = await reader.TryReadFileAsync("setup.cfg", headSha, ct).ConfigureAwait(false);
        return setupCfg is not null ? ParseSetupCfg(setupCfg) : null;
    }

    private LanguageMetadata? ParsePyproject(string content)
    {
        try
        {
            var requiresMatch = Regex.Match(content, @"requires-python\s*=\s*[""']?([^""'\r\n]+)[""']?");
            if (!requiresMatch.Success)
                return null;

            var constraint = requiresMatch.Groups[1].Value.Trim();
            var version = ExtractMajorMinorFromConstraint(constraint);
            if (version is null)
                return null;

            var facts = new List<string> { $"requires-python: {constraint}" };
            if (Regex.IsMatch(content, @"^\[tool\.mypy", RegexOptions.Multiline))
                facts.Add("mypy configured");
            if (Regex.IsMatch(content, @"^\[tool\.ruff", RegexOptions.Multiline))
                facts.Add("ruff configured");
            if (Regex.IsMatch(content, @"^\[tool\.pyright", RegexOptions.Multiline))
                facts.Add("pyright configured");

            return new LanguageMetadata(LanguageId, version, null, facts.AsReadOnly());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse pyproject.toml for Python grounding");
            return null;
        }
    }

    private LanguageMetadata? ParseSetupCfg(string content)
    {
        try
        {
            var match = Regex.Match(content, @"python_requires\s*=\s*([^\r\n]+)");
            if (!match.Success)
                return null;

            var constraint = match.Groups[1].Value.Trim();
            var version = ExtractMajorMinorFromConstraint(constraint);
            return version is null
                ? null
                : new LanguageMetadata(LanguageId, version, null, new[] { $"requires-python: {constraint}" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse setup.cfg for Python grounding");
            return null;
        }
    }

    private static string? ExtractMajorMinor(string version)
    {
        var match = Regex.Match(version, @"^(\d+\.\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractMajorMinorFromConstraint(string constraint)
    {
        var match = Regex.Match(constraint, @"(\d+\.\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }
}
