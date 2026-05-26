namespace ReviewBot.Core.Diff;

public sealed record LineRange(int StartLine, int EndLine)
{
    public bool Contains(int line)
    {
        return line >= StartLine && line <= EndLine;
    }
}
