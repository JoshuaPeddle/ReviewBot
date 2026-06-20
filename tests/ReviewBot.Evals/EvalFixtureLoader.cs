using ReviewBot.Core.Domain;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ReviewBot.Evals;

public sealed class EvalFixtureLoader
{
    private const string FixtureFileName = "fixture.yaml";
    private const string DiffFileName = "diff.patch";
    private const string ExpectedFileName = "expected.yaml";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public EvalFixture Load(string fixtureDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureDirectory);

        if (!Directory.Exists(fixtureDirectory))
        {
            throw new DirectoryNotFoundException($"Eval fixture directory '{fixtureDirectory}' does not exist.");
        }

        var fixturePath = Path.Combine(fixtureDirectory, FixtureFileName);
        var diffPath = Path.Combine(fixtureDirectory, DiffFileName);
        var expectedPath = Path.Combine(fixtureDirectory, ExpectedFileName);

        var metadata = ReadYaml<FixtureMetadataFile>(fixturePath);
        var expected = ReadYaml<ExpectedFindingsFile>(expectedPath);
        var diffPatch = ReadRequiredText(diffPath);

        return new EvalFixture(
            Path.GetFullPath(fixtureDirectory),
            ConvertMetadata(metadata, fixturePath),
            diffPatch,
            ConvertExpected(expected, expectedPath));
    }

    private static T? ReadYaml<T>(string path)
    {
        var yaml = ReadRequiredText(path);

        try
        {
            return Deserializer.Deserialize<T>(yaml);
        }
        catch (YamlException exception)
        {
            throw new InvalidDataException($"Eval fixture YAML '{path}' could not be parsed.", exception);
        }
    }

    private static string ReadRequiredText(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Eval fixture file '{path}' does not exist.", path);
        }

        return File.ReadAllText(path);
    }

    private static FixtureMetadata ConvertMetadata(FixtureMetadataFile? file, string path)
    {
        if (file is null)
        {
            throw new InvalidDataException($"Eval fixture metadata '{path}' is empty.");
        }

        return new FixtureMetadata(
            RequiredString(file.Name, path, "name"),
            RequiredString(file.Category, path, "category"),
            RequiredString(file.Difficulty, path, "difficulty"),
            RequiredString(file.Description, path, "description"));
    }

    private static ExpectedFindings ConvertExpected(ExpectedFindingsFile? file, string path)
    {
        if (file is null)
        {
            throw new InvalidDataException($"Eval fixture expectations '{path}' are empty.");
        }

        if (file.MaxTotalComments is < 0)
        {
            throw new InvalidDataException($"Eval fixture field '{path}:max_total_comments' must be zero or greater.");
        }

        var mustFlag = (file.MustFlag ?? [])
            .Select((entry, index) => ConvertMustFlag(entry, path, index))
            .ToArray();
        var mustNotFlag = (file.MustNotFlag ?? [])
            .Select((entry, index) => ConvertMustNotFlag(entry, path, index))
            .ToArray();

        return new ExpectedFindings(
            mustFlag,
            mustNotFlag,
            file.MaxTotalComments,
            ParseExpectedReviewState(file.ExpectedReviewState, path));
    }

    private static MustFlagExpectation ConvertMustFlag(MustFlagFile file, string path, int index)
    {
        var prefix = $"must_flag[{index}]";
        var lineRange = file.LineRange;
        if (lineRange is null || lineRange.Count != 2 || lineRange[0] <= 0 || lineRange[1] < lineRange[0])
        {
            throw new InvalidDataException(
                $"Eval fixture field '{path}:{prefix}.line_range' must contain a positive [start, end] range.");
        }

        var additional = (file.AdditionalLocations ?? [])
            .Select((entry, locationIndex) => ConvertAdditionalLocation(entry, path, $"{prefix}.additional_locations[{locationIndex}]"))
            .ToArray();

        return new MustFlagExpectation(
            RequiredString(file.Path, path, $"{prefix}.path"),
            lineRange[0],
            lineRange[1],
            ParseSeverity(file.SeverityAtLeast, Severity.Warning, path, $"{prefix}.severity_at_least"),
            RequiredString(file.Topic, path, $"{prefix}.topic"),
            file.MustMentionAny?.Where(keyword => !string.IsNullOrWhiteSpace(keyword)).Select(keyword => keyword.Trim()).ToArray()
                ?? [],
            additional.Length == 0 ? null : additional);
    }

    private static AllowedLocation ConvertAdditionalLocation(AdditionalLocationFile file, string path, string prefix)
    {
        var lineRange = file.LineRange;
        if (lineRange is null || lineRange.Count != 2 || lineRange[0] <= 0 || lineRange[1] < lineRange[0])
        {
            throw new InvalidDataException(
                $"Eval fixture field '{path}:{prefix}.line_range' must contain a positive [start, end] range.");
        }

        return new AllowedLocation(
            RequiredString(file.Path, path, $"{prefix}.path"),
            lineRange[0],
            lineRange[1]);
    }

    private static MustNotFlagExpectation ConvertMustNotFlag(MustNotFlagFile file, string path, int index)
    {
        var prefix = $"must_not_flag[{index}]";
        return new MustNotFlagExpectation(
            RequiredString(file.Path, path, $"{prefix}.path"),
            file.Reason?.Trim() ?? string.Empty,
            ParseSeverity(file.SeverityAbove, Severity.Info, path, $"{prefix}.severity_above"));
    }

    private static Severity ParseSeverity(string? value, Severity defaultValue, string path, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "info" => Severity.Info,
            "warning" => Severity.Warning,
            "error" => Severity.Error,
            _ => throw new InvalidDataException(
                $"Eval fixture field '{path}:{field}' must be one of info, warning, or error.")
        };
    }

    private static string? ParseExpectedReviewState(string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var state = value.Trim().ToUpperInvariant();
        return state switch
        {
            "APPROVE" or "COMMENT" or "REQUEST_CHANGES" => state,
            _ => throw new InvalidDataException(
                $"Eval fixture field '{path}:expected_review_state' must be one of APPROVE, COMMENT, or REQUEST_CHANGES.")
        };
    }

    private static string RequiredString(string? value, string path, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Eval fixture field '{path}:{field}' is required.");
        }

        return value.Trim();
    }

    private sealed class FixtureMetadataFile
    {
        public string? Name { get; set; }

        public string? Category { get; set; }

        public string? Difficulty { get; set; }

        public string? Description { get; set; }
    }

    private sealed class ExpectedFindingsFile
    {
        public List<MustFlagFile>? MustFlag { get; set; }

        public List<MustNotFlagFile>? MustNotFlag { get; set; }

        public int? MaxTotalComments { get; set; }

        public string? ExpectedReviewState { get; set; }
    }

    private sealed class MustFlagFile
    {
        public string? Path { get; set; }

        public List<int>? LineRange { get; set; }

        public string? SeverityAtLeast { get; set; }

        public string? Topic { get; set; }

        public List<string>? MustMentionAny { get; set; }

        public List<AdditionalLocationFile>? AdditionalLocations { get; set; }
    }

    private sealed class AdditionalLocationFile
    {
        public string? Path { get; set; }

        public List<int>? LineRange { get; set; }
    }

    private sealed class MustNotFlagFile
    {
        public string? Path { get; set; }

        public string? Reason { get; set; }

        public string? SeverityAbove { get; set; }
    }
}
