using System.Text;
using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Context;

/// <summary>
/// Chooses which changed files are worth sending in full alongside their diff.
/// </summary>
/// <remarks>
/// Shared by the review worker and the eval harness on purpose. Full-file context is the
/// single largest section of a real review prompt, so if the harness reimplemented this
/// selection the corpus would be measuring a prompt the product never sends.
/// </remarks>
public static class FullFileContextSelector
{
    /// <summary>
    /// Above this share of additions a change is effectively a new file, whose content the
    /// diff already shows in full — fetching it again duplicates content for no context.
    /// </summary>
    public const double MostlyNewFileAdditionRatioThreshold = 0.9;

    /// <summary>
    /// Changed files eligible for full-file context, before the prompt budget is applied.
    /// </summary>
    public static IReadOnlyList<FileChange> SelectCandidates(
        IReadOnlyList<FileChange> files,
        int fullFileMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (fullFileMaxBytes <= 0)
        {
            return [];
        }

        return files
            .Where(file => file.Status != FileChangeStatus.Removed)
            .Where(file => !IsMostlyNewFile(file))
            .Where(file => EstimatePatchBytes(file) <= fullFileMaxBytes)
            .ToArray();
    }

    public static int EstimatePatchBytes(FileChange file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return Encoding.UTF8.GetByteCount(file.Patch);
    }

    public static bool IsMostlyNewFile(FileChange file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var changedLines = file.AdditionsCount + file.DeletionsCount;
        if (changedLines <= 0)
        {
            return false;
        }

        return (double)file.AdditionsCount / changedLines > MostlyNewFileAdditionRatioThreshold;
    }
}
