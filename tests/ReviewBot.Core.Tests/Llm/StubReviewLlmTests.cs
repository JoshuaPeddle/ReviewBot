using FluentAssertions;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;
using ReviewBot.Core.Prompting;

namespace ReviewBot.Core.Tests.Llm;

public class StubReviewLlmTests
{
    [Fact]
    public async Task FixedResultReturnsConfiguredResult()
    {
        var request = CreateRequest();
        var expected = new ReviewResult(
            Summary: "Use the fixed result.",
            Comments:
            [
                new InlineComment(
                    Path: "src/Review.cs",
                    Line: 12,
                    Side: "RIGHT",
                    Body: "This is deterministic.",
                    Severity: Severity.Info)
            ]);
        var llm = new StubReviewLlm(expected);

        var result = await llm.ReviewAsync(request, CancellationToken.None);

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task FunctionResultReceivesRequestAndReturnsComputedResult()
    {
        var request = CreateRequest(prTitle: "Dynamic review");
        ReviewRequest? observedRequest = null;
        var llm = new StubReviewLlm(received =>
        {
            observedRequest = received;
            return new ReviewResult(
                Summary: $"Reviewed {received.PrTitle}.",
                Comments: []);
        });

        var result = await llm.ReviewAsync(request, CancellationToken.None);

        observedRequest.Should().BeSameAs(request);
        result.Should().BeEquivalentTo(new ReviewResult(
            Summary: "Reviewed Dynamic review.",
            Comments: []));
    }

    [Fact]
    public async Task CompleteRawAsyncReturnsConfiguredRawResponse()
    {
        var prompt = new PromptPayload("system", "user");
        var llm = new StubReviewLlm(
            _ => new ReviewResult("unused", []),
            received => $"raw:{received.UserPrompt}");

        var response = await llm.CompleteRawAsync(prompt, CancellationToken.None);

        response.Should().Be("raw:user");
    }

    private static ReviewRequest CreateRequest(string prTitle = "Test PR")
    {
        return new ReviewRequest(
            PrTitle: prTitle,
            PrBody: "Adds a small change.",
            BaseSha: "base",
            HeadSha: "head",
            Files:
            [
                new FileChange(
                    Path: "src/Review.cs",
                    Patch: "@@ -1 +1 @@\n-old\n+new",
                    CommentableLines: new HashSet<int> { 1 },
                    AdditionsCount: 1,
                    DeletionsCount: 1,
                    Status: FileChangeStatus.Modified)
            ],
            Config: ReviewConfig.Default);
    }
}
