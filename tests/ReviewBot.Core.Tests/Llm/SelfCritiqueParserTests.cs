using FluentAssertions;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;

namespace ReviewBot.Core.Tests.Llm;

public class SelfCritiqueParserTests
{
    [Fact]
    public void ValidResponseWithSubsetOfIndicesParses()
    {
        const string rawResponse = """
            {
              "retained_indices": [0, 2],
              "rationale": "The middle comment targets unchanged code."
            }
            """;

        var result = SelfCritiqueParser.Parse(rawResponse, proposedCommentCount: 3);

        result.Should().BeEquivalentTo(new SelfCritiqueResult(
            RetainedIndices: [0, 2],
            Rationale: "The middle comment targets unchanged code."));
    }

    [Fact]
    public void JsonWrappedInFenceParses()
    {
        const string rawResponse = """
            ```json
            {
              "retained_indices": [1],
              "rationale": "Keep one."
            }
            ```
            """;

        var result = SelfCritiqueParser.Parse(rawResponse, proposedCommentCount: 2);

        result.Should().BeEquivalentTo(new SelfCritiqueResult(
            RetainedIndices: [1],
            Rationale: "Keep one."));
    }

    [Fact]
    public void MissingRetainedIndicesReturnsNull()
    {
        const string rawResponse = """
            {
              "rationale": "No indices."
            }
            """;

        var result = SelfCritiqueParser.Parse(rawResponse, proposedCommentCount: 2);

        result.Should().BeNull();
    }

    [Fact]
    public void OutOfRangeIndexReturnsNull()
    {
        const string rawResponse = """
            {
              "retained_indices": [0, 2],
              "rationale": "Index two is not valid for two comments."
            }
            """;

        var result = SelfCritiqueParser.Parse(rawResponse, proposedCommentCount: 2);

        result.Should().BeNull();
    }

    [Fact]
    public void DuplicateIndexReturnsNull()
    {
        const string rawResponse = """
            {
              "retained_indices": [0, 0],
              "rationale": "Duplicated index."
            }
            """;

        var result = SelfCritiqueParser.Parse(rawResponse, proposedCommentCount: 1);

        result.Should().BeNull();
    }

    [Fact]
    public void MalformedJsonReturnsNull()
    {
        const string rawResponse = """
            {
              "retained_indices": [
            }
            """;

        var result = SelfCritiqueParser.Parse(rawResponse, proposedCommentCount: 1);

        result.Should().BeNull();
    }
}
