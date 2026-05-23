using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;
using ReviewBot.Llm.OpenAi;

namespace ReviewBot.Llm.Tests.OpenAi;

public sealed class OpenAiReviewLlmTests
{
    [Fact]
    public async Task ReviewAsyncReturnsParsedResultForCleanJson()
    {
        var client = new FakeOpenAiChatClient(
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
        client.Requests[0].ModelName.Should().Be("gpt-test");
    }

    [Fact]
    public async Task ReviewAsyncRetriesOnceWhenFirstResponseIsMalformed()
    {
        var client = new FakeOpenAiChatClient(
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
        var client = new FakeOpenAiChatClient("not json", "still not json");
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
        var client = new FakeOpenAiChatClient(
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
    public void AddOpenAiReviewLlmRegistersConfiguredProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<OpenAiReviewLlm>>(NullLogger<OpenAiReviewLlm>.Instance);

        services.AddOpenAiReviewLlm(options =>
        {
            options.ApiKey = "test-key";
            options.ModelName = "gpt-test";
            options.BaseUrl = new Uri("http://localhost:11434/v1");
            options.UseJsonMode = false;
        });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IReviewLlm>().Should().BeOfType<OpenAiReviewLlm>();
        provider.GetRequiredService<IConfigurableReviewLlm>().Should().BeOfType<OpenAiReviewLlm>();
        var options = provider.GetRequiredService<OpenAiLlmOptions>();
        options.ModelName.Should().Be("gpt-test");
        options.BaseUrl.Should().Be(new Uri("http://localhost:11434/v1"));
        options.UseJsonMode.Should().BeFalse();
    }

    [Fact]
    public void SdkClientOptionsUseConfiguredCustomEndpoint()
    {
        var options = OpenAiSdkChatClient.CreateClientOptions(new Uri("http://localhost:11434/v1"));

        options.Should().NotBeNull();
        options!.Endpoint.Should().Be(new Uri("http://localhost:11434/v1"));
    }

    [Fact]
    public async Task ReviewAsyncPassesConfiguredCompletionOptionsToClient()
    {
        var client = new FakeOpenAiChatClient(
            """
            {
              "summary": "Done.",
              "comments": []
            }
            """);
        var llm = new OpenAiReviewLlm(
            new OpenAiLlmOptions
            {
                ApiKey = "test-key",
                ModelName = "gpt-test",
                MaxTokens = 1234,
                Temperature = 0.4f,
                UseJsonMode = false
            },
            NullLogger<OpenAiReviewLlm>.Instance,
            client);

        await llm.ReviewAsync(CreateRequest(), CancellationToken.None);

        var request = client.Requests.Should().ContainSingle().Subject;
        request.ModelName.Should().Be("gpt-test");
        request.MaxTokens.Should().Be(1234);
        request.Temperature.Should().Be(0.4f);
        request.UseJsonMode.Should().BeFalse();
    }

    [Fact]
    public async Task WithModelNameUsesOverrideForRequests()
    {
        var client = new FakeOpenAiChatClient(
            """
            {
              "summary": "Done.",
              "comments": []
            }
            """);
        var llm = CreateLlm(client).WithModelName("gpt-override");

        await llm.ReviewAsync(CreateRequest(), CancellationToken.None);

        client.Requests.Should().ContainSingle()
            .Which.ModelName.Should().Be("gpt-override");
    }

    private static OpenAiReviewLlm CreateLlm(FakeOpenAiChatClient client) =>
        new(
            new OpenAiLlmOptions
            {
                ApiKey = "test-key",
                ModelName = "gpt-test"
            },
            NullLogger<OpenAiReviewLlm>.Instance,
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

    private sealed class FakeOpenAiChatClient(params string[] responses) : IOpenAiChatClient
    {
        private readonly Queue<string> responses = new(responses);

        public List<OpenAiChatRequest> Requests { get; } = [];

        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task<string> CompleteChatAsync(OpenAiChatRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            CancellationTokens.Add(ct);

            if (responses.Count == 0)
            {
                throw new InvalidOperationException("No fake OpenAI response was configured.");
            }

            return Task.FromResult(responses.Dequeue());
        }
    }
}
