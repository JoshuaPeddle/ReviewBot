using System.Text;

namespace ReviewBot.Retrieval.Symbols;

internal static class CSharpLexicalSanitizer
{
    public static string StripCommentsAndStrings(
        string line,
        ref bool inBlockComment,
        ref int rawStringQuoteCount)
    {
        var result = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            if (rawStringQuoteCount > 0)
            {
                if (CountConsecutiveQuotes(line, i) >= rawStringQuoteCount)
                {
                    i += rawStringQuoteCount - 1;
                    rawStringQuoteCount = 0;
                }

                continue;
            }

            var current = line[i];
            var next = i + 1 < line.Length ? line[i + 1] : '\0';

            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (current == '/' && next == '/')
            {
                break;
            }

            if (current == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            if (current == '"' && CountConsecutiveQuotes(line, i) >= 3)
            {
                rawStringQuoteCount = CountConsecutiveQuotes(line, i);
                i += rawStringQuoteCount - 1;
                result.Append(' ');
                continue;
            }

            if (current is '"' or '\'')
            {
                i = SkipQuotedLiteral(line, i, current);
                result.Append(' ');
                continue;
            }

            result.Append(current);
        }

        return result.ToString();
    }

    private static int CountConsecutiveQuotes(string line, int start)
    {
        var count = 0;
        while (start + count < line.Length && line[start + count] == '"')
        {
            count++;
        }

        return count;
    }

    private static int SkipQuotedLiteral(string line, int start, char quote)
    {
        var isVerbatim = quote == '"' && start > 0 && line[start - 1] == '@';
        for (var i = start + 1; i < line.Length; i++)
        {
            if (isVerbatim && line[i] == '"' && i + 1 < line.Length && line[i + 1] == '"')
            {
                i++;
                continue;
            }

            if (!isVerbatim && line[i] == '\\')
            {
                i++;
                continue;
            }

            if (line[i] == quote)
            {
                return i;
            }
        }

        return line.Length - 1;
    }
}
