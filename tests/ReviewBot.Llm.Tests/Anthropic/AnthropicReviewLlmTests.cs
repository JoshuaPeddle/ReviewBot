using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;
using ReviewBot.Llm.Anthropic;

namespace ReviewBot.Llm.Tests.Anthropic;

public sealed class AnthropicReviewLlmTests
{
    [Fact]
    public async Task ReviewAsyncReturnsParsedResultForCleanJson()
    {
        var client = new FakeAnthropicClient(
            """
            {
              "summary": "Looks good.",
              "comments": [
                {
                  "path": "src/Widget.cs",
                  "line": 2,
                  "severity": "warning",
                  "body": "Check this edge case."
                }
              ]
            }
            """);
        var llm = CreateLlm(client);

        var result = await llm.ReviewAsync(CreateRequest(), CancellationToken.None);

        result.Summary.Should().Be("Looks good.");
        result.Comments.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new InlineComment(
                Path: "src/Widget.cs",
                Line: 2,
                Side: "RIGHT",
                Body: "Check this edge case.",
                Severity: Severity.Warning));
        client.Requests.Should().ContainSingle()
            .Which.UserMessages.Should().ContainSingle();
        client.Requests[0].ModelName.Should().Be("claude-test");
    }

    [Fact]
    public async Task ReviewAsyncRetriesOnceWhenFirstResponseIsMalformed()
    {
        var client = new FakeAnthropicClient(
            "not json",
            """
            {
              "summary": "Recovered.",
              "comments": []
            }
            """);
        var llm = CreateLlm(client);

        var result = await llm.ReviewAsync(CreateRequest(), CancellationToken.None);

        result.Summary.Should().Be("Recovered.");
        client.Requests.Should().HaveCount(2);
        client.Requests[1].UserMessages.Should().HaveCount(2);
        client.Requests[1].UserMessages[1].Should().Be("Your previous response was not valid JSON. Respond again with ONLY the JSON object.");
    }

    [Fact]
    public async Task ReviewAsyncThrowsLlmResponseExceptionWhenRetryIsMalformed()
    {
        var client = new FakeAnthropicClient("not json", "still not json");
        var llm = CreateLlm(client);

        var act = () => llm.ReviewAsync(CreateRequest(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<LlmResponseException>();
        exception.Which.ParseError.Should().Be("Response did not contain a JSON object.");
        exception.Which.RawResponse.Should().Be("still not json");
        exception.Which.Message.Should().Contain("still not json");
        client.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReviewAsyncPropagatesCancellationTokenToClient()
    {
        using var cts = new CancellationTokenSource();
        var client = new FakeAnthropicClient(
            """
            {
              "summary": "Done.",
              "comments": []
            }
            """);
        var llm = CreateLlm(client);

        await llm.ReviewAsync(CreateRequest(), cts.Token);

        client.CancellationTokens.Should().ContainSingle()
            .Which.Should().Be(cts.Token);
    }

    [Fact]
    public void AddAnthropicReviewLlmRegistersConfiguredProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<AnthropicReviewLlm>>(NullLogger<AnthropicReviewLlm>.Instance);

        services.AddAnthropicReviewLlm(options =>
        {
            options.ApiKey = "test-key";
            options.ModelName = "claude-test";
        });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IReviewLlm>().Should().BeOfType<AnthropicReviewLlm>();
        provider.GetRequiredService<IConfigurableReviewLlm>().Should().BeOfType<AnthropicReviewLlm>();
        provider.GetRequiredService<AnthropicLlmOptions>().ModelName.Should().Be("claude-test");
    }

    [Fact]
    public async Task WithModelNameUsesOverrideForRequests()
    {
        var client = new FakeAnthropicClient(
            """
            {
              "summary": "Done.",
              "comments": []
            }
            """);
        var llm = CreateLlm(client).WithModelName("claude-override");

        await llm.ReviewAsync(CreateRequest(), CancellationToken.None);

        client.Requests.Should().ContainSingle()
            .Which.ModelName.Should().Be("claude-override");
    }

    private static AnthropicReviewLlm CreateLlm(FakeAnthropicClient client) =>
        new(
            new AnthropicLlmOptions
            {
                ApiKey = "test-key",
                ModelName = "claude-test"
            },
            NullLogger<AnthropicReviewLlm>.Instance,
            client);

    private static ReviewRequest CreateRequest() =>
        new(
            PrTitle: "Test PR",
            PrBody: "Adds a widget.",
            BaseSha: "base",
            HeadSha: "head",
            Files:
            [
                new FileChange(
                    Path: "src/Widget.cs",
                    Patch: """
                    @@ -1,2 +1,2 @@
                     public class Widget
                    +{
                    """,
                    CommentableLines: new HashSet<int> { 1, 2 },
                    AdditionsCount: 1,
                    DeletionsCount: 0,
                    Status: FileChangeStatus.Modified)
            ],
            Config: ReviewConfig.Default);

    private sealed class FakeAnthropicClient(params string[] responses) : IAnthropicClient
    {
        private readonly Queue<string> responses = new(responses);

        public List<AnthropicMessageRequest> Requests { get; } = [];

        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task<string> CreateMessageAsync(AnthropicMessageRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            CancellationTokens.Add(ct);

            if (responses.Count == 0)
            {
                throw new InvalidOperationException("No fake Anthropic response was configured.");
            }

            return Task.FromResult(responses.Dequeue());
        }
    }
}
