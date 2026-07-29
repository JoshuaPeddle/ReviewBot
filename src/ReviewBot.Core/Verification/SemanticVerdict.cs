namespace ReviewBot.Core.Verification;

/// <summary>
/// What ground truth had to say about a language-semantics claim.
/// </summary>
public enum SemanticVerdict
{
    /// <summary>
    /// The construct the comment describes was not found, or the file could not be
    /// parsed. The comment survives: silence is not evidence against it.
    /// </summary>
    Unknown = 0,

    /// <summary>The syntax tree contradicts the claim outright.</summary>
    Refuted = 1
}
