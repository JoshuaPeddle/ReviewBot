using System.Text.RegularExpressions;
using ReviewBot.Retrieval.Symbols;

namespace ReviewBot.Retrieval.Indexing;

public sealed class CSharpRepoSymbolParser : IRepoSymbolParser
{
    private static readonly Regex UsingPattern = new(
        @"^\s*using\s+(?:static\s+)?(?:(?:[A-Za-z_][A-Za-z0-9_]*\s*=\s*)?)(?<name>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*;",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex TypeDeclarationPattern = new(
        @"\b(?:class|record|struct|interface|enum|delegate)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex MethodDeclarationPattern = new(
        @"^\s*(?:\[[^\]]+\]\s*)*(?:(?:public|private|protected|internal|static|async|virtual|override|sealed|abstract|partial|extern|unsafe|new)\s+)*(?:[A-Za-z_][A-Za-z0-9_<>,\[\]\?\.]*\s+)+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>\r\n]+>)?\s*\(",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex FieldDeclarationPattern = new(
        @"^\s*(?:(?:public|private|protected|internal|static|readonly|const|required|volatile)\s+)+[A-Za-z_][A-Za-z0-9_<>,\[\]\?\.]*\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:[=;{])",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex MemberAccessPattern = new(
        @"\.\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?<call>\()?",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex InvocationPattern = new(
        @"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>\r\n]+>)?\s*\(",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex IdentifierPattern = new(
        @"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "record", "ref", "return", "sbyte",
        "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this",
        "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "var", "virtual", "void", "volatile", "while", "async", "await", "yield",
        "get", "set", "init", "required", "file"
    };

    private static readonly HashSet<string> NonMemberInvocations = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "nameof", "typeof",
        "sizeof", "default", "checked", "unchecked"
    };

    public bool CanParse(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase);

    // Tuned for local 27B / consumer-hardware deployments where very large
    // retrieval prompts have caused LM Studio / Ollama to OOM or 400. Tighter
    // caps lose long-method context but keep the pipeline reliable. Bump for
    // cloud / larger-context deployments.
    private const int MaxBodyLines = 30;

    public IReadOnlyList<RepoSymbol> Parse(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var rawLines = SplitLines(content).ToArray();
        var sanitized = new string[rawLines.Length];
        var inBlockComment = false;
        var rawStringQuoteCount = 0;
        for (var i = 0; i < rawLines.Length; i++)
        {
            sanitized[i] = CSharpLexicalSanitizer.StripCommentsAndStrings(
                rawLines[i],
                ref inBlockComment,
                ref rawStringQuoteCount);
        }

        var symbols = new List<RepoSymbol>();
        var seen = new HashSet<(string Name, RepoSymbolKind Kind, RepoSymbolRole Role, int Line)>();

        for (var i = 0; i < rawLines.Length; i++)
        {
            var code = sanitized[i];
            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            AddSymbolsFromLine(path, code, i + 1, symbols, seen, rawLines, sanitized);
        }

        return symbols;
    }

    private static (string? Body, int? StartLine, int? EndLine) ExtractMethodBody(
        string[] rawLines,
        string[] sanitized,
        int declarationIndex)
    {
        // Expression-bodied method: `public int X() => 42;` — body is the declaration line.
        if (sanitized[declarationIndex].Contains("=>", StringComparison.Ordinal))
        {
            return (rawLines[declarationIndex], declarationIndex + 1, declarationIndex + 1);
        }

        var depth = 0;
        var bodyStartIndex = -1;
        for (var i = declarationIndex; i < rawLines.Length; i++)
        {
            var line = sanitized[i];
            var opens = CountChar(line, '{');
            var closes = CountChar(line, '}');
            depth += opens - closes;

            if (bodyStartIndex < 0 && opens > 0)
            {
                // Body starts at the declaration line so the signature stays attached.
                bodyStartIndex = declarationIndex;
            }

            if (bodyStartIndex >= 0 && depth <= 0)
            {
                var bodyEndIndex = i;
                var bodyText = JoinLines(rawLines, bodyStartIndex, bodyEndIndex);
                return (bodyText, bodyStartIndex + 1, bodyEndIndex + 1);
            }

            // Cap runaway bodies (very long methods or unbalanced braces).
            if (bodyStartIndex >= 0 && i - bodyStartIndex >= MaxBodyLines - 1)
            {
                var cappedEnd = bodyStartIndex + MaxBodyLines - 1;
                var bodyText = JoinLines(rawLines, bodyStartIndex, cappedEnd);
                return (bodyText, bodyStartIndex + 1, cappedEnd + 1);
            }
        }

        // No body found (interface/abstract method with `;`, or partial file). Leave null.
        return (null, null, null);
    }

    private static int CountChar(string value, char target)
    {
        var count = 0;
        foreach (var ch in value)
        {
            if (ch == target)
            {
                count++;
            }
        }

        return count;
    }

    private static string JoinLines(string[] rawLines, int startIndex, int endIndex)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = startIndex; i <= endIndex; i++)
        {
            if (i > startIndex)
            {
                builder.Append('\n');
            }

            builder.Append(rawLines[i]);
        }

        return builder.ToString();
    }

    private static void AddSymbolsFromLine(
        string path,
        string code,
        int lineNumber,
        List<RepoSymbol> symbols,
        HashSet<(string Name, RepoSymbolKind Kind, RepoSymbolRole Role, int Line)> seen,
        string[] rawLines,
        string[] sanitized)
    {
        var signature = code.Trim();
        var definitionsOnLine = new HashSet<string>(StringComparer.Ordinal);
        var declarationIndex = lineNumber - 1;

        var usingMatch = UsingPattern.Match(code);
        if (usingMatch.Success)
        {
            AddSymbol(usingMatch.Groups["name"].Value, RepoSymbolKind.Import, RepoSymbolRole.Usage);
            return;
        }

        foreach (Match match in TypeDeclarationPattern.Matches(code))
        {
            var name = match.Groups["name"].Value;
            definitionsOnLine.Add(name);
            AddSymbol(name, RepoSymbolKind.Type, RepoSymbolRole.Definition);
        }

        var methodDeclaration = MethodDeclarationPattern.Match(code);
        if (methodDeclaration.Success && !NonMemberInvocations.Contains(methodDeclaration.Groups["name"].Value))
        {
            var name = methodDeclaration.Groups["name"].Value;
            definitionsOnLine.Add(name);
            var (body, bodyStart, bodyEnd) = ExtractMethodBody(rawLines, sanitized, declarationIndex);
            AddSymbol(name, RepoSymbolKind.Method, RepoSymbolRole.Definition, body, bodyStart, bodyEnd);
        }

        var fieldDeclaration = FieldDeclarationPattern.Match(code);
        if (fieldDeclaration.Success)
        {
            var name = fieldDeclaration.Groups["name"].Value;
            definitionsOnLine.Add(name);
            AddSymbol(name, RepoSymbolKind.Field, RepoSymbolRole.Definition);
        }

        foreach (Match match in MemberAccessPattern.Matches(code))
        {
            var name = match.Groups["name"].Value;
            var kind = match.Groups["call"].Success ? RepoSymbolKind.Method : RepoSymbolKind.Field;
            AddSymbol(name, kind, RepoSymbolRole.Usage);
        }

        foreach (Match match in InvocationPattern.Matches(code))
        {
            var name = match.Groups["name"].Value;
            if (definitionsOnLine.Contains(name) ||
                NonMemberInvocations.Contains(name) ||
                IsConstructorInvocation(code, match.Index))
            {
                continue;
            }

            AddSymbol(name, RepoSymbolKind.Method, RepoSymbolRole.Usage);
        }

        foreach (Match match in IdentifierPattern.Matches(code))
        {
            var name = match.Groups["name"].Value;
            if (definitionsOnLine.Contains(name) || IsKeyword(name) || !LooksLikeTypeIdentifier(name))
            {
                continue;
            }

            AddSymbol(name, RepoSymbolKind.Type, RepoSymbolRole.Usage);
        }

        void AddSymbol(
            string name,
            RepoSymbolKind kind,
            RepoSymbolRole role,
            string? body = null,
            int? bodyStartLine = null,
            int? bodyEndLine = null)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                IsKeyword(name) ||
                !seen.Add((name, kind, role, lineNumber)))
            {
                return;
            }

            symbols.Add(new RepoSymbol(name, kind, role, path, lineNumber, signature, body, bodyStartLine, bodyEndLine));
        }
    }

    private static bool IsKeyword(string value) => CSharpKeywords.Contains(value);

    private static bool LooksLikeTypeIdentifier(string value) =>
        value.Length > 0 && char.IsUpper(value[0]);

    private static bool IsConstructorInvocation(string code, int invocationStart)
    {
        var prefix = code[..invocationStart].TrimEnd();
        return prefix.EndsWith("new", StringComparison.Ordinal) ||
               prefix.EndsWith("new?", StringComparison.Ordinal);
    }

    private static IEnumerable<string> SplitLines(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return normalized.Split('\n');
    }
}
