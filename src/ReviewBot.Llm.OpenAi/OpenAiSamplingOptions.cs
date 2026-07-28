namespace ReviewBot.Llm.OpenAi;

/// <summary>
/// Optional sampling knobs beyond <c>temperature</c>. Every value is null by default
/// and is omitted from the request when unset, so an unconfigured provider sends
/// exactly what it sent before and the server's own defaults apply.
/// </summary>
/// <remarks>
/// <see cref="TopP"/> and <see cref="PresencePenalty"/> are part of the OpenAI chat
/// schema. <see cref="TopK"/>, <see cref="MinP"/> and <see cref="RepetitionPenalty"/>
/// are not — they are vendor extensions understood by the local servers we target
/// (vLLM, SGLang, Ollama), so they are patched directly into the request body.
/// Servers that don't recognise them ignore the extra fields.
/// </remarks>
public sealed record OpenAiSamplingOptions
{
    public float? TopP { get; set; }

    public int? TopK { get; set; }

    public float? MinP { get; set; }

    public float? PresencePenalty { get; set; }

    public float? RepetitionPenalty { get; set; }

    /// <summary>
    /// RNG seed for sampling. Fixing it makes an A/B comparison paired — both sides draw
    /// the same randomness, so a difference is attributable to the change under test
    /// rather than to sampling luck. Servers treat this as best-effort: batching can
    /// still perturb results, so a fixed seed narrows run-to-run variance without
    /// guaranteeing bit-identical output.
    /// </summary>
    public long? Seed { get; set; }

    /// <summary>
    /// True when at least one knob is set, i.e. the request body needs modifying.
    /// </summary>
    public bool HasAnyValue =>
        this.TopP is not null ||
        this.TopK is not null ||
        this.MinP is not null ||
        this.PresencePenalty is not null ||
        this.RepetitionPenalty is not null ||
        this.Seed is not null;
}
