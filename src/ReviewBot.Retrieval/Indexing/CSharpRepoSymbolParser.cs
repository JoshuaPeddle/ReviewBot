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

    public IReadOnlyList<RepoSymbol> Parse(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var symbols = new List<RepoSymbol>();
        var seen = new HashSet<(string Name, RepoSymbolKind Kind, RepoSymbolRole Role, int Line)>();
        var inBlockComment = false;
        var rawStringQuoteCount = 0;
        var lineNumber = 0;

        foreach (var rawLine in SplitLines(content))
        {
            lineNumber++;
            var code = CSharpLexicalSanitizer.StripCommentsAndStrings(
                rawLine,
                ref inBlockComment,
                ref rawStringQuoteCount);

            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            AddSymbolsFromLine(path, code, lineNumber, symbols, seen);
        }

        return symbols;
    }

    private static void AddSymbolsFromLine(
        string path,
        string code,
        int lineNumber,
        List<RepoSymbol> symbols,
        HashSet<(string Name, RepoSymbolKind Kind, RepoSymbolRole Role, int Line)> seen)
    {
        var signature = code.Trim();
        var definitionsOnLine = new HashSet<string>(StringComparer.Ordinal);

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
            AddSymbol(name, RepoSymbolKind.Method, RepoSymbolRole.Definition);
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

        void AddSymbol(string name, RepoSymbolKind kind, RepoSymbolRole role)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                IsKeyword(name) ||
                !seen.Add((name, kind, role, lineNumber)))
            {
                return;
            }

            symbols.Add(new RepoSymbol(name, kind, role, path, lineNumber, signature));
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
