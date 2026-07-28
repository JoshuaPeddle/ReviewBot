using System.Text;
using System.Text.RegularExpressions;

namespace ReviewBot.Retrieval.Indexing;

internal sealed class GlobMatcher
{
    private readonly IReadOnlyList<Regex> patterns;

    public GlobMatcher(IReadOnlyList<string> patterns)
    {
        this.patterns = patterns
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(pattern => new Regex(
                "^" + ToRegex(pattern.Replace('\\', '/')) + "$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(100)))
            .ToArray();
    }

    public bool IsMatch(string path)
    {
        var normalized = path.Replace('\\', '/');
        return patterns.Any(pattern => pattern.IsMatch(normalized));
    }

    private static string ToRegex(string glob)
    {
        var builder = new StringBuilder(glob.Length * 2);
        for (var i = 0; i < glob.Length; i++)
        {
            var current = glob[i];
            if (current == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    builder.Append(".*");
                    i++;
                }
                else
                {
                    builder.Append("[^/]*");
                }

                continue;
            }

            builder.Append(current == '?' ? "[^/]" : Regex.Escape(current.ToString()));
        }

        return builder.ToString();
    }
}
