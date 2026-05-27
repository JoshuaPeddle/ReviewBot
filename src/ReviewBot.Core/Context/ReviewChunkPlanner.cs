using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Context;

public sealed class ReviewChunkPlanner
{
    private readonly IPromptTokenEstimator tokenEstimator;

    public ReviewChunkPlanner(IPromptTokenEstimator tokenEstimator)
    {
        this.tokenEstimator = tokenEstimator ?? throw new ArgumentNullException(nameof(tokenEstimator));
    }

    public int EstimateDiffTokens(IReadOnlyList<FileChange> files, int maxPatchLines)
    {
        ArgumentNullException.ThrowIfNull(files);

        var tokens = 0;
        foreach (var file in files)
        {
            tokens += EstimateFileTokens(file, maxPatchLines);
        }

        return tokens;
    }

    public IReadOnlyList<ReviewChunk> PlanChunks(
        IReadOnlyList<FileChange> files,
        int contentBudgetTokens,
        double headroom,
        int maxChunks,
        int maxPatchLines)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (contentBudgetTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentBudgetTokens), contentBudgetTokens, "Content budget cannot be negative.");
        }

        if (headroom <= 0 || headroom > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(headroom), headroom, "Chunk headroom must be greater than 0 and less than or equal to 1.");
        }

        if (maxChunks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChunks), maxChunks, "Maximum chunk count must be positive.");
        }

        if (files.Count == 0)
        {
            return [];
        }

        var targetTokens = Math.Max(1, (int)Math.Floor(contentBudgetTokens * headroom));
        var planned = new List<PlannedChunk>();
        var currentFiles = new List<FileChange>();
        var currentTokens = 0;

        foreach (var entry in files
            .Select(file => new FileTokenEstimate(file, EstimateFileTokens(file, maxPatchLines)))
            .OrderBy(file => DirectoryPrefix(file.File.Path), StringComparer.Ordinal)
            .ThenBy(file => file.File.Path, StringComparer.Ordinal))
        {
            if (currentFiles.Count > 0 && currentTokens + entry.Tokens > targetTokens)
            {
                planned.Add(new PlannedChunk(currentFiles.ToArray(), currentTokens));
                currentFiles.Clear();
                currentTokens = 0;
            }

            currentFiles.Add(entry.File);
            currentTokens += entry.Tokens;
        }

        if (currentFiles.Count > 0)
        {
            planned.Add(new PlannedChunk(currentFiles.ToArray(), currentTokens));
        }

        var capped = planned.Take(maxChunks).ToArray();
        return capped
            .Select((chunk, index) => new ReviewChunk(index + 1, capped.Length, chunk.Files, chunk.EstimatedTokens))
            .ToArray();
    }

    private int EstimateFileTokens(FileChange file, int maxPatchLines) =>
        tokenEstimator.EstimateTokens(
            $"{file.Path} {file.Status} +{file.AdditionsCount} -{file.DeletionsCount}\n{TakePatchLines(file.Patch, maxPatchLines)}");

    private static string TakePatchLines(string patch, int maxPatchLines)
    {
        if (maxPatchLines <= 0 || string.IsNullOrEmpty(patch))
        {
            return string.Empty;
        }

        return string.Join('\n', patch
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Take(maxPatchLines));
    }

    private static string DirectoryPrefix(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash <= 0 ? string.Empty : path[..lastSlash];
    }

    private sealed record FileTokenEstimate(FileChange File, int Tokens);

    private sealed record PlannedChunk(IReadOnlyList<FileChange> Files, int EstimatedTokens);
}
