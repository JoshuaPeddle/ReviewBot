using FluentAssertions;
using OpenAI.Chat;
using System.ClientModel.Primitives;
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
        client.Requests[0].ResponseFormat.Should().Be("text");
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
    public async Task ReviewAsyncThrowsWhenRepairIsMalformed()
    {
        var client = new FakeOpenAiChatClient("not json", "still not json");
        var llm = CreateLlm(client);

        // Returning an empty result here would be posted as a clean review, telling the
        // author nothing is wrong with a PR that was never successfully reviewed.
        await llm.Invoking(l => l.ReviewAsync(CreateRequest(), CancellationToken.None))
            .Should().ThrowAsync<LlmResponseUnusableException>()
            .WithMessage("*could not be parsed as review JSON*");

        client.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReviewAsyncThrowsWithBudgetHintWhenResponseIsEmptyButTokensWereConsumed()
    {
        // A reasoning model that burns its whole output allowance on chain-of-thought
        // reports completion tokens and returns no content. Observed on Qwen3.6-27B.
        var client = new FakeOpenAiChatClient(new OpenAiChatResult(
            string.Empty,
            new LlmTokenUsage(PromptTokens: 29970, CompletionTokens: 4606)));
        var llm = CreateLlm(client);

        await llm.Invoking(l => l.ReviewAsync(CreateRequest(), CancellationToken.None))
            .Should().ThrowAsync<LlmResponseUnusableException>()
            .WithMessage("*4606 completion tokens*response_reserve_tokens*");

        // No repair round-trip: there is nothing to repair in an empty string.
        client.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task ReviewAsyncThrowsWhenResponseIsEmptyWithNoTokens()
    {
        var client = new FakeOpenAiChatClient(new OpenAiChatResult(
            "   ",
            new LlmTokenUsage(PromptTokens: 100, CompletionTokens: 0)));
        var llm = CreateLlm(client);

        await llm.Invoking(l => l.ReviewAsync(CreateRequest(), CancellationToken.None))
            .Should().ThrowAsync<LlmResponseUnusableException>()
            .WithMessage("*no completion tokens at all*");

        client.Requests.Should().HaveCount(1);
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
    public async Task ReviewAsyncAttachesTokenUsageToResult()
    {
        var usage = new LlmTokenUsage(PromptTokens: 150, CompletionTokens: 75, CachedPromptTokens: 20);
        var client = new FakeOpenAiChatClient(
            new OpenAiChatResult("""{"summary": "Done.", "comments": []}""", usage));
        var llm = CreateLlm(client);

        var result = await llm.ReviewAsync(CreateRequest(), CancellationToken.None);

        result.TokenUsage.Should().BeEquivalentTo(usage);
    }

    [Fact]
    public async Task ReviewAsyncAccumulatesUsageAcrossPrimaryAndRepairCalls()
    {
        var firstUsage = new LlmTokenUsage(PromptTokens: 100, CompletionTokens: 50);
        var repairUsage = new LlmTokenUsage(PromptTokens: 120, CompletionTokens: 60);
        var client = new FakeOpenAiChatClient(
            new OpenAiChatResult("not json", firstUsage),
            new OpenAiChatResult("""{"summary": "Recovered.", "comments": []}""", repairUsage));
        var llm = CreateLlm(client);

        var result = await llm.ReviewAsync(CreateRequest(), CancellationToken.None);

        result.TokenUsage.Should().BeEquivalentTo(new LlmTokenUsage(220, 110));
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

    [Fact]
    public void ChatRequestExceptionNamesEndpointAndModelOnNotFound()
    {
        // A stale base URL used to surface as a bare "Service request failed", which
        // named neither the endpoint nor the model that was actually called.
        var exception = new OpenAiChatRequestException(
            404,
            responseBody: null,
            innerException: new InvalidOperationException("boom"),
            baseUrl: new Uri("https://stale.example.com/v1"),
            modelName: "Qwen/Qwen3.6-27B-FP8");

        exception.Message.Should().Contain("https://stale.example.com/v1");
        exception.Message.Should().Contain("Qwen/Qwen3.6-27B-FP8");
        exception.Message.Should().Contain("REVIEWBOT__OpenAi__BaseUrl");
        exception.Message.Should().Contain("REVIEWBOT__OpenAi__ModelName");
    }

    [Fact]
    public void ChatRequestExceptionPointsAtTheApiKeyOnUnauthorized()
    {
        var exception = new OpenAiChatRequestException(
            401,
            responseBody: "invalid api key",
            innerException: new InvalidOperationException("boom"),
            baseUrl: new Uri("https://api.example.com/v1"),
            modelName: "some-model");

        exception.Message.Should().Contain("invalid api key");
        exception.Message.Should().Contain("REVIEWBOT__OpenAi__ApiKey");
    }

    [Fact]
    public void ChatRequestExceptionLeavesOtherStatusesUnembellished()
    {
        var exception = new OpenAiChatRequestException(
            500,
            responseBody: "upstream exploded",
            innerException: new InvalidOperationException("boom"),
            baseUrl: new Uri("https://api.example.com/v1"),
            modelName: "some-model");

        exception.Message.Should().Be(
            "OpenAI-compatible request failed with status 500: upstream exploded");
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

    [Fact]
    public void ApplySamplingWritesEveryKnobIntoTheRequestBody()
    {
        var options = new ChatCompletionOptions { Temperature = 0.6f };

        OpenAiSdkChatClient.ApplySampling(
            options,
            new OpenAiSamplingOptions
            {
                TopP = 0.95f,
                TopK = 20,
                MinP = 0.0f,
                PresencePenalty = 0.0f,
                RepetitionPenalty = 1.0f,
                Seed = 12345L
            });

        // top_k, min_p and repetition_penalty are outside the OpenAI schema, so the only
        // way to prove they reach the server is to inspect the serialized body.
        var body = ModelReaderWriter.Write(options).ToString();

        body.Should().Contain("\"temperature\":0.6");
        body.Should().Contain("\"top_p\":0.95");
        body.Should().Contain("\"top_k\":20");
        body.Should().Contain("\"min_p\":0");
        body.Should().Contain("\"presence_penalty\":0");
        body.Should().Contain("\"repetition_penalty\":1");
        body.Should().Contain("\"seed\":12345");
    }

    [Fact]
    public void ApplySamplingLeavesRequestUntouchedWhenUnset()
    {
        var options = new ChatCompletionOptions { Temperature = 0.2f };

        OpenAiSdkChatClient.ApplySampling(options, sampling: null);
        OpenAiSdkChatClient.ApplySampling(options, new OpenAiSamplingOptions());

        var body = ModelReaderWriter.Write(options).ToString();

        options.TopP.Should().BeNull();
        options.PresencePenalty.Should().BeNull();
#pragma warning disable OPENAI001
        options.Seed.Should().BeNull();
#pragma warning restore OPENAI001
        body.Should().NotContain("seed");
        body.Should().NotContain("top_p");
        body.Should().NotContain("top_k");
        body.Should().NotContain("min_p");
        body.Should().NotContain("presence_penalty");
        body.Should().NotContain("repetition_penalty");
    }

    [Fact]
    public void ApplySamplingWritesOnlyTheKnobsThatAreSet()
    {
        var options = new ChatCompletionOptions();

        OpenAiSdkChatClient.ApplySampling(options, new OpenAiSamplingOptions { TopK = 20 });

        var body = ModelReaderWriter.Write(options).ToString();

        body.Should().Contain("\"top_k\":20");
        body.Should().NotContain("min_p");
        body.Should().NotContain("repetition_penalty");
    }

    [Fact]
    public void SamplingOptionsReportWhetherAnyKnobIsSet()
    {
        new OpenAiSamplingOptions().HasAnyValue.Should().BeFalse();
        new OpenAiSamplingOptions { MinP = 0.0f }.HasAnyValue.Should().BeTrue();
        new OpenAiSamplingOptions { RepetitionPenalty = 1.0f }.HasAnyValue.Should().BeTrue();
        new OpenAiSamplingOptions { Seed = 0L }.HasAnyValue.Should().BeTrue();
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

    [Fact]
    public async Task UsesRequestMaxOutputTokensWhenSet()
    {
        var client = new FakeOpenAiChatClient("""{"summary":"ok","comments":[]}""");
        var llm = CreateLlm(client);

        await llm.ReviewAsync(CreateRequest() with { MaxOutputTokens = 777 }, CancellationToken.None);

        client.Requests.Should().ContainSingle().Which.MaxTokens.Should().Be(777);
    }

    [Fact]
    public async Task FallsBackToConfiguredMaxTokensWhenRequestOutputUnset()
    {
        var client = new FakeOpenAiChatClient("""{"summary":"ok","comments":[]}""");
        var llm = CreateLlm(client);

        await llm.ReviewAsync(CreateRequest(), CancellationToken.None);

        // OpenAiLlmOptions default MaxTokens (no per-request override supplied).
        client.Requests.Should().ContainSingle().Which.MaxTokens.Should().Be(4096);
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
