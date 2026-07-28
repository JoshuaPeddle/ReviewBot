using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Context;

public sealed record ReviewChunk(
    int Index,
    int TotalChunks,
    IReadOnlyList<FileChange> Files,
    int EstimatedTokens);
