using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;
using ReviewBot.Core.Prompting;
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
    public async Task ReviewAsyncRepairsMalformedFirstResponse()
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
        client.Requests[1].SystemPrompt.Should().StartWith("Your previous response was not valid JSON.");
        client.Requests[1].SystemPrompt.Should().Contain("\"summary\"");
        client.Requests[1].SystemPrompt.Should().Contain("\"comments\"");
        client.Requests[1].UserMessages.Should().Equal("not json");
    }

    [Fact]
    public async Task ReviewAsyncReturnsEmptyResultWhenRepairIsMalformed()
    {
        var client = new FakeAnthropicClient("not json", "still not json");
        var llm = CreateLlm(client);

        var result = await llm.ReviewAsync(CreateRequest(), CancellationToken.None);

        result.Should().BeEquivalentTo(new ReviewResult(string.Empty, []));
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
    public async Task ReviewAsyncRetriesTransientHttpFailuresTwice()
    {
        var delays = new List<TimeSpan>();
        var client = new FakeAnthropicClient(
            new HttpRequestException("timeout"),
            new HttpRequestException("gateway reset"),
            """
            {
              "summary": "Recovered.",
              "comments": []
            }
            """);
        var llm = CreateLlm(client, delay =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        var result = await llm.ReviewAsync(CreateRequest(), CancellationToken.None);

        result.Summary.Should().Be("Recovered.");
        client.Requests.Should().HaveCount(3);
        delays.Should().Equal(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200));
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

    [Fact]
    public async Task CompleteRawAsyncSendsPromptAndReturnsUnparsedResponse()
    {
        var client = new FakeAnthropicClient("""{"retained_indices":[0],"rationale":"ok"}""");
        var llm = CreateLlm(client);
        var prompt = new PromptPayload("critique system", "critique user");

        var response = await llm.CompleteRawAsync(prompt, CancellationToken.None);

        response.Should().Be("""{"retained_indices":[0],"rationale":"ok"}""");
        var request = client.Requests.Should().ContainSingle().Subject;
        request.SystemPrompt.Should().Be("critique system");
        request.UserMessages.Should().Equal("critique user");
    }

    private static AnthropicReviewLlm CreateLlm(FakeAnthropicClient client) =>
        CreateLlm(client, _ => Task.CompletedTask);

    private static AnthropicReviewLlm CreateLlm(
        FakeAnthropicClient client,
        Func<TimeSpan, Task> delayAsync) =>
        new(
            new AnthropicLlmOptions
            {
                ApiKey = "test-key",
                ModelName = "claude-test"
            },
            NullLogger<AnthropicReviewLlm>.Instance,
            client,
            (delay, _) => delayAsync(delay));

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

    private sealed class FakeAnthropicClient(params object[] outcomes) : IAnthropicClient
    {
        private readonly Queue<object> outcomes = new(outcomes);

        public List<AnthropicMessageRequest> Requests { get; } = [];

        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task<string> CreateMessageAsync(AnthropicMessageRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            CancellationTokens.Add(ct);

            if (outcomes.Count == 0)
            {
                throw new InvalidOperationException("No fake Anthropic response was configured.");
            }

            var outcome = outcomes.Dequeue();
            return outcome switch
            {
                string response => Task.FromResult(response),
                Exception exception => Task.FromException<string>(exception),
                _ => throw new InvalidOperationException($"Unsupported fake outcome type {outcome.GetType().FullName}."),
            };
        }
    }
}
