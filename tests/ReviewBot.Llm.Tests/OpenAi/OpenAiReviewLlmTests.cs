using FluentAssertions;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;
using ReviewBot.Core.Prompting;
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
        client.Requests[0].ResponseFormat.Should().Be("json_object");
        client.Requests[0].IncludeContextRequestsInJsonSchema.Should().BeFalse();
    }

    [Fact]
    public async Task ReviewAsyncRepairsMalformedFirstResponse()
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
        client.Requests[1].SystemPrompt.Should().StartWith("Your previous response was not valid JSON.");
        client.Requests[1].SystemPrompt.Should().Contain("\"summary\"");
        client.Requests[1].SystemPrompt.Should().Contain("\"comments\"");
        client.Requests[1].UserMessages.Should().Equal("not json");
    }

    [Fact]
    public async Task ReviewAsyncReturnsEmptyResultWhenRepairIsMalformed()
    {
        var client = new FakeOpenAiChatClient("not json", "still not json");
        var llm = CreateLlm(client);

        var result = await llm.ReviewAsync(CreateRequest(), CancellationToken.None);

        result.Should().BeEquivalentTo(new ReviewResult(string.Empty, []));
        client.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReviewAsyncRecordsParseFailureMetricWithRepairOutcome()
    {
        var measurements = new List<(string provider, string repaired, long value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ReviewBotLlmMetrics.MeterName &&
                instrument.Name == "reviewbot.llm.parse_failures_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var tagArray = tags.ToArray();
            measurements.Add((
                tagArray.FirstOrDefault(t => t.Key == "provider").Value?.ToString() ?? "",
                tagArray.FirstOrDefault(t => t.Key == "repaired").Value?.ToString() ?? "",
                value));
        });
        listener.Start();
        var client = new FakeOpenAiChatClient(
            "not json",
            """
            {
              "summary": "Recovered.",
              "comments": []
            }
            """);
        var llm = CreateLlm(client);

        await llm.ReviewAsync(CreateRequest(), CancellationToken.None);

        measurements.Should().ContainSingle(measurement => measurement.provider == "openai")
            .Which.Should().Be(("openai", "true", 1L));
    }

    [Fact]
    public async Task CompleteRawAsyncRecordsTokenUsageWithPhase()
    {
        var measurements = new List<(string direction, string phase, int value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ReviewBotLlmMetrics.MeterName &&
                instrument.Name == "reviewbot.llm.tokens")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, value, tags, _) =>
        {
            var tagArray = tags.ToArray();
            measurements.Add((
                tagArray.FirstOrDefault(t => t.Key == "direction").Value?.ToString() ?? "",
                tagArray.FirstOrDefault(t => t.Key == "phase").Value?.ToString() ?? "",
                value));
        });
        listener.Start();
        var client = new FakeOpenAiChatClient(new OpenAiChatResult(
            """{"retained_indices":[0],"rationale":"ok"}""",
            new LlmTokenUsage(PromptTokens: 11, CompletionTokens: 7)));
        var llm = CreateLlm(client);

        await llm.CompleteRawAsync(new PromptPayload("system", "user"), CancellationToken.None, "self_critique");

        measurements.Should().BeEquivalentTo(
        [
            ("prompt", "self_critique", 11),
            ("completion", "self_critique", 7)
        ]);
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
    public async Task ReviewAsyncRetriesTransientHttpFailuresTwice()
    {
        var delays = new List<TimeSpan>();
        var client = new FakeOpenAiChatClient(
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
    public void AddOpenAiReviewLlmRegistersConfiguredProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<OpenAiReviewLlm>>(NullLogger<OpenAiReviewLlm>.Instance);

        services.AddOpenAiReviewLlm(options =>
        {
            options.ApiKey = "test-key";
            options.ModelName = "gpt-test";
            options.BaseUrl = new Uri("http://localhost:11434/v1");
            options.ResponseFormat = "text";
        });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IReviewLlm>().Should().BeOfType<OpenAiReviewLlm>();
        provider.GetRequiredService<IConfigurableReviewLlm>().Should().BeOfType<OpenAiReviewLlm>();
        var options = provider.GetRequiredService<OpenAiLlmOptions>();
        options.ModelName.Should().Be("gpt-test");
        options.BaseUrl.Should().Be(new Uri("http://localhost:11434/v1"));
        options.ResponseFormat.Should().Be("text");
    }

    [Fact]
    public void SdkClientOptionsUseConfiguredCustomEndpoint()
    {
        var options = OpenAiSdkChatClient.CreateClientOptions(new Uri("http://localhost:11434/v1"), 60);

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
                ResponseFormat = "text"
            },
            NullLogger<OpenAiReviewLlm>.Instance,
            client);

        await llm.ReviewAsync(CreateRequest(), CancellationToken.None);

        var request = client.Requests.Should().ContainSingle().Subject;
        request.ModelName.Should().Be("gpt-test");
        request.MaxTokens.Should().Be(1234);
        request.Temperature.Should().Be(0.4f);
        request.ResponseFormat.Should().Be("text");
    }

    [Fact]
    public async Task ReviewAsyncSendsConfiguredJsonSchemaResponseFormat()
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
                ResponseFormat = "json_schema"
            },
            NullLogger<OpenAiReviewLlm>.Instance,
            client);

        await llm.ReviewAsync(CreateRequest(agenticContext: true), CancellationToken.None);

        var request = client.Requests.Should().ContainSingle().Subject;
        request.ResponseFormat.Should().Be("json_schema");
        request.IncludeContextRequestsInJsonSchema.Should().BeTrue();
    }

    [Fact]
    public async Task ReviewAsyncSendsConfiguredTextResponseFormat()
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
                ResponseFormat = "text"
            },
            NullLogger<OpenAiReviewLlm>.Instance,
            client);

        await llm.ReviewAsync(CreateRequest(), CancellationToken.None);

        client.Requests.Should().ContainSingle()
            .Which.ResponseFormat.Should().Be("text");
    }

    [Fact]
    public async Task CompleteRawAsyncAlwaysUsesTextResponseFormat()
    {
        var client = new FakeOpenAiChatClient("""{"retained_indices":[0],"rationale":"ok"}""");
        var llm = new OpenAiReviewLlm(
            new OpenAiLlmOptions
            {
                ApiKey = "test-key",
                ModelName = "gpt-test",
                ResponseFormat = "json_schema"
            },
            NullLogger<OpenAiReviewLlm>.Instance,
            client);

        await llm.CompleteRawAsync(new PromptPayload("system", "user"), CancellationToken.None);

        client.Requests.Should().ContainSingle()
            .Which.ResponseFormat.Should().Be("text");
    }

    [Fact]
    public void ResponseFormatRejectsUnsupportedValues()
    {
        var options = new OpenAiLlmOptions();

        var act = () => options.ResponseFormat = "xml";

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Accepted values: json_object, json_schema, text*");
    }

    [Theory]
    [InlineData("json_object", true)]
    [InlineData("json_schema", true)]
    [InlineData("text", false)]
    public void SdkClientCreatesExpectedResponseFormat(string responseFormat, bool expectedSdkFormat)
    {
        var format = OpenAiSdkChatClient.CreateResponseFormat(responseFormat, includeContextRequests: true);

        (format is not null).Should().Be(expectedSdkFormat);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReviewJsonSchemaIncludesExpectedFields(bool includeContextRequests)
    {
        var schema = OpenAiSdkChatClient.BuildReviewJsonSchema(includeContextRequests);

        schema.Should().Contain("\"summary\"");
        schema.Should().Contain("\"comments\"");
        schema.Should().Contain("\"confidence\"");
        schema.Should().Contain("\"severity\"");
        schema.Should().Contain("\"required\": [\"path\", \"line\", \"severity\", \"confidence\", \"body\"]");
        schema.Contains("\"context_requests\"", StringComparison.Ordinal).Should().Be(includeContextRequests);
    }

    [Fact]
    public async Task CompleteRawAsyncSendsPromptAndReturnsUnparsedResponse()
    {
        var client = new FakeOpenAiChatClient("""{"retained_indices":[0],"rationale":"ok"}""");
        var llm = CreateLlm(client);
        var prompt = new PromptPayload("critique system", "critique user");

        var response = await llm.CompleteRawAsync(prompt, CancellationToken.None);

        response.Should().Be("""{"retained_indices":[0],"rationale":"ok"}""");
        var request = client.Requests.Should().ContainSingle().Subject;
        request.SystemPrompt.Should().Be("critique system");
        request.UserMessages.Should().Equal("critique user");
        request.ResponseFormat.Should().Be("text");
        request.IncludeContextRequestsInJsonSchema.Should().BeFalse();
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
        CreateLlm(client, _ => Task.CompletedTask);

    private static OpenAiReviewLlm CreateLlm(
        FakeOpenAiChatClient client,
        Func<TimeSpan, Task> delayAsync) =>
        new(
            new OpenAiLlmOptions
            {
                ApiKey = "test-key",
                ModelName = "gpt-test"
            },
            NullLogger<OpenAiReviewLlm>.Instance,
            client,
            (delay, _) => delayAsync(delay));

    private static ReviewRequest CreateRequest(bool agenticContext = false) =>
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
            Config: ReviewConfig.Default with
            {
                Review = ReviewConfig.Default.Review with { AgenticContext = agenticContext }
            });

    private sealed class FakeOpenAiChatClient(params object[] outcomes) : IOpenAiChatClient
    {
        private readonly Queue<object> outcomes = new(outcomes);

        public List<OpenAiChatRequest> Requests { get; } = [];

        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task<OpenAiChatResult> CompleteChatAsync(OpenAiChatRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            CancellationTokens.Add(ct);

            if (outcomes.Count == 0)
            {
                throw new InvalidOperationException("No fake OpenAI response was configured.");
            }

            var outcome = outcomes.Dequeue();
            return outcome switch
            {
                string response => Task.FromResult(new OpenAiChatResult(response, null)),
                OpenAiChatResult response => Task.FromResult(response),
                Exception exception => Task.FromException<OpenAiChatResult>(exception),
                _ => throw new InvalidOperationException($"Unsupported fake outcome type {outcome.GetType().FullName}."),
            };
        }
    }
}
