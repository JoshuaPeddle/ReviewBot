namespace ReviewBot.Grounding.Detection;

public interface IRepoContentReader
{
    Task<string?> TryReadFileAsync(string path, string sha, CancellationToken ct);
    Task<IReadOnlyList<string>> ListRootFilesAsync(string sha, CancellationToken ct);
}
