using System.Globalization;
using System.Text.RegularExpressions;
using ReviewBot.Core.Domain;

namespace ReviewBot.Retrieval.Symbols;

public sealed class CSharpDiffSymbolExtractor : IDiffSymbolExtractor
{
    private static readonly Regex HunkHeaderPattern = new(
        @"^@@\s+-\d+(?:,\d+)?\s+\+(?<newStart>\d+)(?:,(?<newCount>\d+))?\s+@@(?:\s.*)?$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex UsingPattern = new(
        @"^\s*using\s+(?:static\s+)?(?:(?:[A-Za-z_][A-Za-z0-9_]*\s*=\s*)?)(?<name>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*;",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex TypeDeclarationPattern = new(
        @"\b(?:class|record|struct|interface|enum|delegate)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b",
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

    public IReadOnlyList<FileDiffSymbols> Extract(IReadOnlyList<FileChange> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var results = new List<FileDiffSymbols>();
        foreach (var file in files)
        {
            if (file.Status == FileChangeStatus.Removed ||
                string.IsNullOrWhiteSpace(file.Patch) ||
                !IsCSharpPath(file.Path))
            {
                continue;
            }

            var symbols = ExtractFileSymbols(file.Patch);
            if (symbols.Count > 0)
            {
                results.Add(new FileDiffSymbols(file.Path, symbols));
            }
        }

        return results;
    }

    private static bool IsCSharpPath(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<DiffSymbol> ExtractFileSymbols(string patch)
    {
        var symbols = new List<DiffSymbol>();
        var seen = new HashSet<(string Name, DiffSymbolKind Kind)>();
        int? nextNewLine = null;
        var inBlockComment = false;
        var rawStringQuoteCount = 0;

        foreach (var rawLine in SplitLines(patch))
        {
            if (rawLine.StartsWith("@@", StringComparison.Ordinal))
            {
                nextNewLine = ParseNewStartLine(rawLine);
                inBlockComment = false;
                rawStringQuoteCount = 0;
                continue;
            }

            if (nextNewLine is null ||
                rawLine.Length == 0 ||
                rawLine.StartsWith('\\') ||
                rawLine.StartsWith("+++", StringComparison.Ordinal) ||
                rawLine.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            var prefix = rawLine[0];
            if (prefix == '-')
            {
                continue;
            }

            if (prefix is not '+' and not ' ')
            {
                continue;
            }

            var lineNumber = nextNewLine.Value;
            nextNewLine++;

            var code = CSharpLexicalSanitizer.StripCommentsAndStrings(
                rawLine[1..],
                ref inBlockComment,
                ref rawStringQuoteCount);
            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            AddSymbolsFromLine(code, lineNumber, symbols, seen);
        }

        return symbols;
    }

    private static void AddSymbolsFromLine(
        string code,
        int lineNumber,
        List<DiffSymbol> symbols,
        HashSet<(string Name, DiffSymbolKind Kind)> seen)
    {
        var usingMatch = UsingPattern.Match(code);
        if (usingMatch.Success)
        {
            AddSymbol(usingMatch.Groups["name"].Value, DiffSymbolKind.Import, lineNumber, symbols, seen);
            return;
        }

        foreach (Match match in TypeDeclarationPattern.Matches(code))
        {
            AddSymbol(match.Groups["name"].Value, DiffSymbolKind.Type, lineNumber, symbols, seen);
        }

        foreach (Match match in MemberAccessPattern.Matches(code))
        {
            var name = match.Groups["name"].Value;
            var kind = match.Groups["call"].Success ? DiffSymbolKind.Method : DiffSymbolKind.Field;
            AddSymbol(name, kind, lineNumber, symbols, seen);
        }

        foreach (Match match in InvocationPattern.Matches(code))
        {
            var name = match.Groups["name"].Value;
            if (NonMemberInvocations.Contains(name) || IsConstructorInvocation(code, match.Index))
            {
                continue;
            }

            AddSymbol(name, DiffSymbolKind.Method, lineNumber, symbols, seen);
        }

        foreach (Match match in IdentifierPattern.Matches(code))
        {
            var name = match.Groups["name"].Value;
            if (IsKeyword(name) || !LooksLikeTypeIdentifier(name) || SeenWithAnyKind(seen, name))
            {
                continue;
            }

            AddSymbol(name, DiffSymbolKind.Type, lineNumber, symbols, seen);
        }
    }

    private static void AddSymbol(
        string name,
        DiffSymbolKind kind,
        int lineNumber,
        List<DiffSymbol> symbols,
        HashSet<(string Name, DiffSymbolKind Kind)> seen)
    {
        if (string.IsNullOrWhiteSpace(name) || IsKeyword(name) || !seen.Add((name, kind)))
        {
            return;
        }

        symbols.Add(new DiffSymbol(name, kind, lineNumber));
    }

    private static bool IsKeyword(string value) => CSharpKeywords.Contains(value);

    private static bool LooksLikeTypeIdentifier(string value) =>
        value.Length > 0 && (char.IsUpper(value[0]) || value[0] == 'I' && value.Length > 1 && char.IsUpper(value[1]));

    private static bool SeenWithAnyKind(HashSet<(string Name, DiffSymbolKind Kind)> seen, string name) =>
        seen.Contains((name, DiffSymbolKind.Type)) ||
        seen.Contains((name, DiffSymbolKind.Method)) ||
        seen.Contains((name, DiffSymbolKind.Field)) ||
        seen.Contains((name, DiffSymbolKind.Import));

    private static bool IsConstructorInvocation(string code, int invocationStart)
    {
        var prefix = code[..invocationStart].TrimEnd();
        return prefix.EndsWith("new", StringComparison.Ordinal) ||
               prefix.EndsWith("new?", StringComparison.Ordinal);
    }

    private static int ParseNewStartLine(string hunkHeader)
    {
        var match = HunkHeaderPattern.Match(hunkHeader);
        if (!match.Success)
        {
            throw new FormatException($"Malformed unified diff hunk header: {hunkHeader}");
        }

        return int.Parse(
            match.Groups["newStart"].Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture);
    }

    private static IEnumerable<string> SplitLines(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return normalized.Split('\n');
    }
}
