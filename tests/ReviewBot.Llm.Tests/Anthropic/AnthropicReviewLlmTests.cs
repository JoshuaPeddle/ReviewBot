using FluentAssertions;
using Anthropic.SDK.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewBot.Core.Context;
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
        client.Requests[0].EnablePromptCaching.Should().BeTrue();
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
        client.Requests[1].EnablePromptCaching.Should().BeFalse();
    }

    [Fact]
    public async Task ReviewAsyncReturnsEmptyResultWhenRepairIsMalformed()
    {
        var client = new FakeAnthropicClient("not json", "still not json");
        var llm = CreateLlm(client);

        var result = await llm.ReviewAsync(CreateRequest(), CancellationToken.None);

        result.Should().BeEquivalentTo(new ReviewResult(string.Empty, []) { RawLlmResponse = "not json" });
        client.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReviewAsyncAttachesTokenUsageToResult()
    {
        var usage = new LlmTokenUsage(PromptTokens: 150, CompletionTokens: 75, CachedPromptTokens: 20);
        var client = new FakeAnthropicClient(
            new AnthropicMessageResult(
                """{"summary": "Done.", "comments": []}""",
                usage));
        var llm = CreateLlm(client);

        var result = await llm.ReviewAsync(CreateRequest(), CancellationToken.None);

        result.TokenUsage.Should().BeEquivalentTo(usage);
    }

    [Fact]
    public async Task ReviewAsyncAccumulatesUsageAcrossPrimaryAndRepairCalls()
    {
        var firstUsage = new LlmTokenUsage(PromptTokens: 100, CompletionTokens: 50);
        var repairUsage = new LlmTokenUsage(PromptTokens: 120, CompletionTokens: 60);
        var client = new FakeAnthropicClient(
            new AnthropicMessageResult("not json", firstUsage),
            new AnthropicMessageResult("""{"summary": "Recovered.", "comments": []}""", repairUsage));
        var llm = CreateLlm(client);

        var result = await llm.ReviewAsync(CreateRequest(), CancellationToken.None);

        result.TokenUsage.Should().BeEquivalentTo(new LlmTokenUsage(220, 110));
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
        services.AddSingleton<ILogger<AnthropicTokenEstimator>>(NullLogger<AnthropicTokenEstimator>.Instance);

        services.AddAnthropicReviewLlm(options =>
        {
            options.ApiKey = "test-key";
            options.ModelName = "claude-test";
        });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IReviewLlm>().Should().BeOfType<AnthropicReviewLlm>();
        provider.GetRequiredService<IConfigurableReviewLlm>().Should().BeOfType<AnthropicReviewLlm>();
        provider.GetRequiredService<AnthropicLlmOptions>().ModelName.Should().Be("claude-test");
        provider.GetServices<IProviderPromptTokenEstimator>()
            .Should().ContainSingle(estimator => estimator.ProviderName == "anthropic");
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
        request.EnablePromptCaching.Should().BeTrue();
    }

    [Fact]
    public async Task ReviewAsyncHonorsDisabledPromptCachingOption()
    {
        var client = new FakeAnthropicClient(
            """
            {
              "summary": "Done.",
              "comments": []
            }
            """);
        var llm = CreateLlm(client, promptCachingEnabled: false);

        await llm.ReviewAsync(CreateRequest(), CancellationToken.None);

        client.Requests.Should().ContainSingle()
            .Which.EnablePromptCaching.Should().BeFalse();
    }

    [Fact]
    public void BuildParametersEnablesFineGrainedPromptCachingOnSystemPrompt()
    {
        var request = new AnthropicMessageRequest(
            SystemPrompt: "system",
            UserMessages: ["user"],
            ModelName: "claude-test",
            MaxTokens: 123,
            Temperature: 0.1m,
            EnablePromptCaching: true);

        var parameters = AnthropicSdkClient.BuildParameters(request);

        parameters.PromptCaching.Should().Be(PromptCacheType.FineGrained);
        parameters.System.Should().ContainSingle()
            .Which.CacheControl.Should().BeEquivalentTo(new CacheControl { Type = CacheControlType.ephemeral });
        parameters.Messages.Should().ContainSingle()
            .Which.Content.Should().ContainSingle()
            .Which.CacheControl.Should().BeNull();
    }

    [Fact]
    public void BuildParametersLeavesPromptCachingUnsetWhenDisabled()
    {
        var request = new AnthropicMessageRequest(
            SystemPrompt: "system",
            UserMessages: ["user"],
            ModelName: "claude-test",
            MaxTokens: 123,
            Temperature: 0.1m,
            EnablePromptCaching: false);

        var parameters = AnthropicSdkClient.BuildParameters(request);

        parameters.PromptCaching.Should().Be(PromptCacheType.None);
        parameters.System.Should().ContainSingle()
            .Which.CacheControl.Should().BeNull();
    }

    [Fact]
    public void BuildTokenCountParametersMapsSystemAndUserMessages()
    {
        var request = new AnthropicTokenCountRequest(
            ModelName: "claude-test",
            SystemPrompt: "system",
            UserMessages: ["first", "second"]);

        var parameters = AnthropicSdkClient.BuildTokenCountParameters(request);

        parameters.Model.Should().Be("claude-test");
        parameters.System.Should().ContainSingle()
            .Which.Text.Should().Be("system");
        parameters.Messages.Select(message => ((TextContent)message.Content.Single()).Text)
            .Should().Equal("first", "second");
    }

    [Fact]
    public void AnthropicTokenEstimatorUsesHeuristicBelowThreshold()
    {
        var client = new FakeAnthropicClient();
        var estimator = CreateTokenEstimator(
            client,
            thresholdTokens: 10,
            heuristicTokens: 9);

        var tokens = estimator.EstimateTokens(new ModelConfig("anthropic", "claude-test", null), "large-ish");

        tokens.Should().Be(9);
        client.TokenCountRequests.Should().BeEmpty();
    }

    [Fact]
    public void AnthropicTokenEstimatorCallsCountTokensForLargePrompts()
    {
        var client = new FakeAnthropicClient(123);
        var estimator = CreateTokenEstimator(
            client,
            thresholdTokens: 10,
            heuristicTokens: 10);

        var tokens = estimator.EstimateTokens(new ModelConfig("anthropic", "claude-override", null), "large prompt");

        tokens.Should().Be(123);
        client.TokenCountRequests.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new AnthropicTokenCountRequest(
                ModelName: "claude-override",
                SystemPrompt: null,
                UserMessages: ["large prompt"]));
    }

    [Fact]
    public void AnthropicTokenEstimatorFallsBackToHeuristicWhenCountFails()
    {
        var client = new FakeAnthropicClient(new HttpRequestException("unavailable"));
        var estimator = CreateTokenEstimator(
            client,
            thresholdTokens: 10,
            heuristicTokens: 10);

        var tokens = estimator.EstimateTokens(new ModelConfig("anthropic", "claude-test", null), "large prompt");

        tokens.Should().Be(10);
        client.TokenCountRequests.Should().ContainSingle();
    }

    private static AnthropicReviewLlm CreateLlm(FakeAnthropicClient client) =>
        CreateLlm(client, _ => Task.CompletedTask);

    private static AnthropicReviewLlm CreateLlm(FakeAnthropicClient client, bool promptCachingEnabled) =>
        CreateLlm(client, _ => Task.CompletedTask, promptCachingEnabled);

    private static AnthropicReviewLlm CreateLlm(
        FakeAnthropicClient client,
        Func<TimeSpan, Task> delayAsync) =>
        CreateLlm(client, delayAsync, promptCachingEnabled: true);

    private static AnthropicReviewLlm CreateLlm(
        FakeAnthropicClient client,
        Func<TimeSpan, Task> delayAsync,
        bool promptCachingEnabled) =>
        new(
            new AnthropicLlmOptions
            {
                ApiKey = "test-key",
                ModelName = "claude-test",
                PromptCachingEnabled = promptCachingEnabled
            },
            NullLogger<AnthropicReviewLlm>.Instance,
            client,
            (delay, _) => delayAsync(delay));

    private static AnthropicTokenEstimator CreateTokenEstimator(
        FakeAnthropicClient client,
        int thresholdTokens,
        int heuristicTokens) =>
        new(
            new AnthropicLlmOptions
            {
                ApiKey = "test-key",
                ModelName = "claude-test",
                TokenCountingHeuristicThresholdTokens = thresholdTokens
            },
            new FixedTokenEstimator(heuristicTokens),
            NullLogger<AnthropicTokenEstimator>.Instance,
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

    private sealed class FakeAnthropicClient(params object[] outcomes) : IAnthropicClient
    {
        private readonly Queue<object> outcomes = new(outcomes);

        public List<AnthropicMessageRequest> Requests { get; } = [];

        public List<AnthropicTokenCountRequest> TokenCountRequests { get; } = [];

        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task<AnthropicMessageResult> CreateMessageAsync(AnthropicMessageRequest request, CancellationToken ct)
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
                string response => Task.FromResult(new AnthropicMessageResult(response, null)),
                AnthropicMessageResult result => Task.FromResult(result),
                Exception exception => Task.FromException<AnthropicMessageResult>(exception),
                _ => throw new InvalidOperationException($"Unsupported fake outcome type {outcome.GetType().FullName}."),
            };
        }

        public Task<int> CountTokensAsync(AnthropicTokenCountRequest request, CancellationToken ct)
        {
            TokenCountRequests.Add(request);
            CancellationTokens.Add(ct);

            if (outcomes.Count == 0)
            {
                throw new InvalidOperationException("No fake Anthropic token count was configured.");
            }

            var outcome = outcomes.Dequeue();
            return outcome switch
            {
                int tokens => Task.FromResult(tokens),
                Exception exception => Task.FromException<int>(exception),
                _ => throw new InvalidOperationException($"Unsupported fake outcome type {outcome.GetType().FullName}."),
            };
        }
    }

    private sealed class FixedTokenEstimator(int tokens) : IPromptTokenEstimator
    {
        public int EstimateTokens(string? text) => tokens;
    }
}
