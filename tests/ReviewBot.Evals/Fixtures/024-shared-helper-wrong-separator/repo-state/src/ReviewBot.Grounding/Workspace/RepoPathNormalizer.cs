namespace ReviewBot.Grounding.Workspace;

/// <summary>
/// Normalises repository-relative paths before they are joined onto a workspace root.
/// </summary>
public static class RepoPathNormalizer
{
    /// <summary>
    /// Strips a leading separator so the path joins onto a root instead of replacing it.
    /// </summary>
    public static string StripLeadingSeparator(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return path.TrimStart(Path.DirectorySeparatorChar);
    }
}
