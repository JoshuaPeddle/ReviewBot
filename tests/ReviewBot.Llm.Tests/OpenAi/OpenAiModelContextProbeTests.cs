using FluentAssertions;
using ReviewBot.Llm.OpenAi;

namespace ReviewBot.Llm.Tests.OpenAi;

public sealed class OpenAiModelContextProbeTests
{
    // Real vLLM /v1/models shape (trimmed).
    private const string VllmModels =
        """
        {"object":"list","data":[
          {"id":"qwen3.6-27b-autoround","object":"model","owned_by":"vllm","max_model_len":32768}
        ]}
        """;

    [Fact]
    public void ParsesMaxModelLenForMatchingModel()
    {
        OpenAiModelContextProbe.TryParseMaxModelLen(VllmModels, "qwen3.6-27b-autoround", out var tokens)
            .Should().BeTrue();
        tokens.Should().Be(32768);
    }

    [Fact]
    public void PicksTheMatchingModelAmongSeveral()
    {
        const string json =
            """
            {"data":[
              {"id":"small","max_model_len":8192},
              {"id":"big","max_model_len":131072}
            ]}
            """;

        OpenAiModelContextProbe.TryParseMaxModelLen(json, "big", out var tokens).Should().BeTrue();
        tokens.Should().Be(131072);
    }

    [Fact]
    public void ReturnsFalseWhenModelNotListed()
    {
        OpenAiModelContextProbe.TryParseMaxModelLen(VllmModels, "some-other-model", out var tokens)
            .Should().BeFalse();
        tokens.Should().Be(0);
    }

    [Fact]
    public void ReturnsFalseWhenModelLacksMaxModelLen()
    {
        // OpenAI proper and most non-vLLM servers don't advertise a context window.
        const string json = """{"data":[{"id":"gpt-5.1","object":"model"}]}""";

        OpenAiModelContextProbe.TryParseMaxModelLen(json, "gpt-5.1", out var tokens).Should().BeFalse();
        tokens.Should().Be(0);
    }

    [Theory]
    [InlineData("""{"object":"list"}""")]
    [InlineData("""{"data":"not-an-array"}""")]
    [InlineData("""{"data":[{"id":"m","max_model_len":0}]}""")]
    [InlineData("""{"data":[{"id":"m","max_model_len":-1}]}""")]
    public void ReturnsFalseForMalformedOrNonPositive(string json)
    {
        OpenAiModelContextProbe.TryParseMaxModelLen(json, "m", out var tokens).Should().BeFalse();
        tokens.Should().Be(0);
    }
}
