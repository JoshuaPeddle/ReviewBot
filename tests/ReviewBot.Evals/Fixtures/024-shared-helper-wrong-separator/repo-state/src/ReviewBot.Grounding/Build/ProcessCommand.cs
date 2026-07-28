namespace ReviewBot.Grounding.Build;

internal sealed record ProcessCommand(string FileName, IReadOnlyList<string> Arguments)
{
    public static bool TryParse(string? commandLine, out ProcessCommand? command, out string? error)
    {
        command = null;
        error = null;

        if (string.IsNullOrWhiteSpace(commandLine))
        {
            error = "command is empty";
            return false;
        }

        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;
        var escaping = false;

        foreach (var c in commandLine.Trim())
        {
            if (escaping)
            {
                if (char.IsWhiteSpace(c) || c is '"' or '\'' or '\\')
                    current.Append(c);
                else
                    current.Append('\\').Append(c);

                escaping = false;
                continue;
            }

            if (c == '\\')
            {
                escaping = true;
                continue;
            }

            if (quote is not null)
            {
                if (c == quote)
                    quote = null;
                else
                    current.Append(c);

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (escaping)
            current.Append('\\');

        if (quote is not null)
        {
            error = "command contains an unterminated quote";
            return false;
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        if (tokens.Count == 0)
        {
            error = "command is empty";
            return false;
        }

        command = new ProcessCommand(tokens[0], tokens.Skip(1).ToArray());
        return true;
    }
}
