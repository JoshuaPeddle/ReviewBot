namespace ReviewBot.Grounding.Detection;

public interface ILanguageDetector
{
    string LanguageId { get; }
    bool CanDetect(IReadOnlyList<string> rootFileNames);
    Task<LanguageMetadata?> ExtractMetadataAsync(IRepoContentReader reader, string headSha, CancellationToken ct);
}
