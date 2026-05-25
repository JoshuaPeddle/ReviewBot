using ReviewBot.Core.Domain;

namespace ReviewBot.Evals;

public sealed class RuleBasedScorer
{
    public RuleBasedScore Score(EvalFixture fixture, ReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(result);

        var comments = result.Comments;
        var matchedCommentIndexes = new HashSet<int>();
        var mustFlagResults = new List<MustFlagScore>();

        foreach (var expectation in fixture.Expected.MustFlag)
        {
            var matchIndex = FindMustFlagMatch(expectation, comments, matchedCommentIndexes);
            if (matchIndex is null)
            {
                mustFlagResults.Add(new MustFlagScore(
                    expectation.Path,
                    expectation.StartLine,
                    expectation.EndLine,
                    expectation.Topic,
                    Passed: false,
                    MatchedCommentBody: null,
                    FailureReason: BuildMustFlagFailure(expectation)));
                continue;
            }

            matchedCommentIndexes.Add(matchIndex.Value);
            mustFlagResults.Add(new MustFlagScore(
                expectation.Path,
                expectation.StartLine,
                expectation.EndLine,
                expectation.Topic,
                Passed: true,
                MatchedCommentBody: comments[matchIndex.Value].Body,
                FailureReason: null));
        }

        var mustNotFlagResults = fixture.Expected.MustNotFlag
            .Select(expectation => ScoreMustNotFlag(expectation, comments))
            .ToArray();

        var falsePositiveComments = comments
            .Select((comment, index) => new { comment, index })
            .Where(entry => !matchedCommentIndexes.Contains(entry.index))
            .Where(entry => !IsAllowedByMustNotFlag(entry.comment, fixture.Expected.MustNotFlag))
            .Select(entry => entry.comment)
            .ToArray();

        var truePositives = mustFlagResults.Count(result => result.Passed);
        var falseNegatives = mustFlagResults.Count(result => !result.Passed);
        var falsePositives = falsePositiveComments.Length;
        var precision = Divide(truePositives, truePositives + falsePositives);
        var recall = Divide(truePositives, fixture.Expected.MustFlag.Count);
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        var withinMaxComments = fixture.Expected.MaxTotalComments is null ||
            comments.Count <= fixture.Expected.MaxTotalComments.Value;
        var passed = falseNegatives == 0 &&
            falsePositives == 0 &&
            mustNotFlagResults.All(result => result.Passed) &&
            withinMaxComments;

        return new RuleBasedScore(
            passed,
            comments.Count,
            fixture.Expected.MaxTotalComments,
            truePositives,
            falsePositives,
            falseNegatives,
            precision,
            recall,
            f1,
            mustFlagResults,
            mustNotFlagResults,
            falsePositiveComments);
    }

    private static int? FindMustFlagMatch(
        MustFlagExpectation expectation,
        IReadOnlyList<InlineComment> comments,
        HashSet<int> matchedCommentIndexes)
    {
        for (var index = 0; index < comments.Count; index++)
        {
            if (matchedCommentIndexes.Contains(index))
            {
                continue;
            }

            if (MatchesMustFlag(expectation, comments[index]))
            {
                return index;
            }
        }

        return null;
    }

    private static bool MatchesMustFlag(MustFlagExpectation expectation, InlineComment comment)
    {
        return string.Equals(comment.Path, expectation.Path, StringComparison.Ordinal) &&
            comment.Line >= expectation.StartLine &&
            comment.Line <= expectation.EndLine &&
            comment.Severity >= expectation.SeverityAtLeast &&
            MentionsAnyKeyword(comment.Body, expectation.MustMentionAny);
    }

    private static bool MentionsAnyKeyword(string body, IReadOnlyList<string> keywords)
    {
        return keywords.Count == 0 ||
            keywords.Any(keyword => body.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static MustNotFlagScore ScoreMustNotFlag(
        MustNotFlagExpectation expectation,
        IReadOnlyList<InlineComment> comments)
    {
        var violatingComments = comments
            .Where(comment => string.Equals(comment.Path, expectation.Path, StringComparison.Ordinal))
            .Where(comment => comment.Severity > expectation.SeverityAbove)
            .ToArray();

        return new MustNotFlagScore(
            expectation.Path,
            expectation.Reason,
            expectation.SeverityAbove,
            Passed: violatingComments.Length == 0,
            violatingComments);
    }

    private static bool IsAllowedByMustNotFlag(
        InlineComment comment,
        IReadOnlyList<MustNotFlagExpectation> expectations)
    {
        return expectations.Any(expectation =>
            string.Equals(comment.Path, expectation.Path, StringComparison.Ordinal) &&
            comment.Severity <= expectation.SeverityAbove);
    }

    private static string BuildMustFlagFailure(MustFlagExpectation expectation)
    {
        var keywords = expectation.MustMentionAny.Count == 0
            ? "no keyword constraint"
            : $"one of: {string.Join(", ", expectation.MustMentionAny)}";
        return $"No comment matched {expectation.Path}:{expectation.StartLine}-{expectation.EndLine} " +
            $"at severity {expectation.SeverityAtLeast} or higher mentioning {keywords}.";
    }

    private static double Divide(int numerator, int denominator)
    {
        return denominator == 0 ? 1 : (double)numerator / denominator;
    }
}
