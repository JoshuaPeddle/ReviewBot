namespace ReviewBot.Api.Tracing;

public sealed class ReviewTrace
{
    public required string DeliveryId { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public required string Owner { get; init; }
    public required string Repo { get; init; }
    public required int PrNumber { get; init; }
    public required string HeadSha { get; init; }
    public required string BaseSha { get; init; }
    public required string PrTitle { get; init; }
    public required string TriggerReason { get; init; }
    public required string ReviewType { get; init; }
    public required string ModelProvider { get; init; }
    public required string ModelName { get; init; }
    public required IReadOnlyList<string> FilesReviewed { get; init; }
    public required int ChunkCount { get; init; }
    public int RetrievalSnippetsCount { get; init; }
    public TraceBudgetSnapshot? PromptBudget { get; init; }
    public required string ResultSummary { get; init; }
    public required IReadOnlyList<TraceComment> CandidateComments { get; init; }
    public required IReadOnlyList<TraceDroppedComment> DroppedComments { get; init; }
    public required IReadOnlyList<TraceComment> FinalComments { get; init; }
    public TraceLlmTokenUsage? TokenUsage { get; init; }
    public decimal? EstimatedCostUsd { get; init; }
    public IReadOnlyList<TraceChunk>? ChunkTraces { get; init; }
    public TraceTimings? Timings { get; init; }
}

public sealed class TraceBudgetSnapshot
{
    public required int ModelContextLimitTokens { get; init; }
    public required int SystemPromptTokens { get; init; }
    public required int GroundingTokens { get; init; }
    public required int ResponseReserveTokens { get; init; }
    public required int ContentBudgetTokens { get; init; }
    public required int ConsumedContentTokens { get; init; }
    public required int RemainingContentTokens { get; init; }
    public required IReadOnlyList<TraceBudgetSectionSnapshot> ConsumedSections { get; init; }
}

public sealed class TraceBudgetSectionSnapshot
{
    public required string Name { get; init; }
    public required int Tokens { get; init; }
}

public sealed class TraceComment
{
    public required string Path { get; init; }
    public required int Line { get; init; }
    public required string Side { get; init; }
    public required string Body { get; init; }
    public required string Severity { get; init; }
    public required string Confidence { get; init; }
}

public sealed class TraceDroppedComment
{
    public required string Path { get; init; }
    public required int Line { get; init; }
    public required string Side { get; init; }
    public required string Body { get; init; }
    public required string Severity { get; init; }
    public required string Confidence { get; init; }
    public required string Reason { get; init; }
}

public sealed class TraceLlmTokenUsage
{
    public required int PromptTokens { get; init; }
    public required int CompletionTokens { get; init; }
    public int CachedPromptTokens { get; init; }
}

public sealed class TraceChunk
{
    public required int ChunkIndex { get; init; }
    public required int TotalChunks { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
    public required double ElapsedMs { get; init; }
    public int PromptSystemBytes { get; init; }
    public int PromptUserBytes { get; init; }
    public string? PromptSystem { get; init; }
    public string? PromptUser { get; init; }
    public int RawLlmResponseBytes { get; init; }
    public string? RawLlmResponse { get; init; }
    public TraceAgenticContext? AgenticContext { get; init; }
}

public sealed class TraceAgenticContext
{
    public required IReadOnlyList<TraceContextRequest> Requested { get; init; }
    public required IReadOnlyList<TraceContextRequest> Accepted { get; init; }
    public required IReadOnlyList<string> FetchedPaths { get; init; }
    public required IReadOnlyList<TraceDropCount> DropCounts { get; init; }
    public bool SecondPassRan { get; init; }
}

public sealed class TraceContextRequest
{
    public required string Path { get; init; }
    public string? Reason { get; init; }
}

public sealed class TraceDropCount
{
    public required string Reason { get; init; }
    public required int Count { get; init; }
}

public sealed class TraceTimings
{
    public double GroundingMs { get; init; }
    public double RetrievalMs { get; init; }
    public double FullFileContextMs { get; init; }
    public double TotalMs { get; init; }
}
