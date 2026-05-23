using FluentAssertions;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;

namespace ReviewBot.Core.Tests.Llm;

public class LlmResultParserTests
{
    [Fact]
    public void CleanJsonParses()
    {
        const string rawResponse = """
            {
              "summary": "Looks good overall.",
              "comments": [
                {
                  "path": "src/Review.cs",
                  "line": 12,
                  "side": "LEFT",
                  "severity": "warning",
                  "body": "This can throw when input is null."
                }
              ],
              "ignored": true
            }
            """;

        var result = LlmResultParser.Parse(rawResponse);

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
        result.Value.Should().BeEquivalentTo(new ReviewResult(
            Summary: "Looks good overall.",
            Comments:
            [
                new InlineComment(
                    Path: "src/Review.cs",
                    Line: 12,
                    Side: "LEFT",
                    Body: "This can throw when input is null.",
                    Severity: Severity.Warning)
            ]));
    }

    [Fact]
    public void JsonWrappedInFenceParses()
    {
        const string rawResponse = """
            ```json
            {
              "summary": "One issue found.",
              "comments": [
                {
                  "path": "src/Review.cs",
                  "line": 5,
                  "severity": "error",
                  "body": "Guard this branch."
                }
              ]
            }
            ```
            """;

        var result = LlmResultParser.Parse(rawResponse);

        result.Success.Should().BeTrue();
        result.Value!.Comments.Should().ContainSingle()
            .Which.Should().Be(new InlineComment(
                Path: "src/Review.cs",
                Line: 5,
                Side: "RIGHT",
                Body: "Guard this branch.",
                Severity: Severity.Error));
    }

    [Fact]
    public void LeadingProseParsesByFindingFirstJsonObject()
    {
        const string rawResponse = """
            Here is the review:
            {
              "summary": "The string body may contain braces like {this}.",
              "comments": []
            }
            Thanks.
            """;

        var result = LlmResultParser.Parse(rawResponse);

        result.Success.Should().BeTrue();
        result.Value!.Summary.Should().Be("The string body may contain braces like {this}.");
        result.Value.Comments.Should().BeEmpty();
    }

    [Fact]
    public void MissingSummaryReturnsFailure()
    {
        const string rawResponse = """
            {
              "comments": []
            }
            """;

        var result = LlmResultParser.Parse(rawResponse);

        result.Success.Should().BeFalse();
        result.Value.Should().BeNull();
        result.Error.Should().Be("Response JSON must include a string summary.");
    }

    [Fact]
    public void InvalidCommentIsDroppedWhileValidCommentsAreRetained()
    {
        const string rawResponse = """
            {
              "summary": "Mixed comments.",
              "comments": [
                {
                  "path": "src/Valid.cs",
                  "line": 2,
                  "body": "Keep this."
                },
                {
                  "path": "src/Invalid.cs",
                  "line": 0,
                  "body": "Drop this."
                },
                {
                  "path": "src/AlsoValid.cs",
                  "line": 8,
                  "severity": "WARNING",
                  "body": "Keep this too."
                }
              ]
            }
            """;

        var result = LlmResultParser.Parse(rawResponse);

        result.Success.Should().BeTrue();
        result.Value!.Comments.Should().Equal(
            new InlineComment("src/Valid.cs", 2, "RIGHT", "Keep this.", Severity.Info),
            new InlineComment("src/AlsoValid.cs", 8, "RIGHT", "Keep this too.", Severity.Warning));
    }

    [Fact]
    public void UnknownSeverityFallsBackToInfo()
    {
        const string rawResponse = """
            {
              "summary": "Unknown severity.",
              "comments": [
                {
                  "path": "src/Review.cs",
                  "line": 3,
                  "severity": "critical",
                  "body": "Still parse this."
                }
              ]
            }
            """;

        var result = LlmResultParser.Parse(rawResponse);

        result.Success.Should().BeTrue();
        result.Value!.Comments.Should().ContainSingle()
            .Which.Severity.Should().Be(Severity.Info);
    }

    [Fact]
    public void ConfidenceLowParsedFromResponse()
    {
        const string rawResponse = """
            {
              "summary": "One issue.",
              "comments": [
                {
                  "path": "src/Review.cs",
                  "line": 5,
                  "body": "Speculative suggestion.",
                  "confidence": "low"
                }
              ]
            }
            """;

        var result = LlmResultParser.Parse(rawResponse);

        result.Success.Should().BeTrue();
        result.Value!.Comments.Should().ContainSingle()
            .Which.Confidence.Should().Be(Confidence.Low);
    }

    [Fact]
    public void ConfidenceMediumParsedFromResponse()
    {
        const string rawResponse = """
            {
              "summary": "One issue.",
              "comments": [
                {
                  "path": "src/Review.cs",
                  "line": 5,
                  "body": "Likely an issue.",
                  "confidence": "medium"
                }
              ]
            }
            """;

        var result = LlmResultParser.Parse(rawResponse);

        result.Success.Should().BeTrue();
        result.Value!.Comments.Should().ContainSingle()
            .Which.Confidence.Should().Be(Confidence.Medium);
    }

    [Fact]
    public void MissingConfidenceFieldDefaultsToHigh()
    {
        const string rawResponse = """
            {
              "summary": "One issue.",
              "comments": [
                {
                  "path": "src/Review.cs",
                  "line": 5,
                  "body": "Definite bug."
                }
              ]
            }
            """;

        var result = LlmResultParser.Parse(rawResponse);

        result.Success.Should().BeTrue();
        result.Value!.Comments.Should().ContainSingle()
            .Which.Confidence.Should().Be(Confidence.High);
    }

    [Fact]
    public void ContextRequestsAreParsedWhenPresent()
    {
        const string rawResponse = """
            {
              "summary": "Need more context.",
              "comments": [],
              "context_requests": [
                {
                  "path": "src/Contracts/IReviewStore.cs",
                  "reason": "Verify the interface contract."
                },
                {
                  "path": "src/Workers/ReviewWorker.cs"
                }
              ]
            }
            """;

        var result = LlmResultParser.Parse(rawResponse);

        result.Success.Should().BeTrue();
        result.Value!.ContextRequests.Should().Equal(
            new ContextRequest("src/Contracts/IReviewStore.cs", "Verify the interface contract."),
            new ContextRequest("src/Workers/ReviewWorker.cs", null));
    }

    [Fact]
    public void MissingContextRequestsDefaultsToEmptyList()
    {
        const string rawResponse = """
            {
              "summary": "No context needed.",
              "comments": []
            }
            """;

        var result = LlmResultParser.Parse(rawResponse);

        result.Success.Should().BeTrue();
        result.Value!.ContextRequests.Should().BeEmpty();
    }

    [Fact]
    public void InvalidContextRequestsAreDropped()
    {
        const string rawResponse = """
            {
              "summary": "Mixed context.",
              "comments": [],
              "context_requests": [
                {
                  "path": "src/Valid.cs",
                  "reason": "Relevant type."
                },
                {
                  "reason": "Missing path."
                },
                "not an object"
              ]
            }
            """;

        var result = LlmResultParser.Parse(rawResponse);

        result.Success.Should().BeTrue();
        result.Value!.ContextRequests.Should().Equal(
            new ContextRequest("src/Valid.cs", "Relevant type."));
    }

    [Fact]
    public void UnknownConfidenceValueDefaultsToHigh()
    {
        const string rawResponse = """
            {
              "summary": "One issue.",
              "comments": [
                {
                  "path": "src/Review.cs",
                  "line": 5,
                  "body": "Unknown confidence.",
                  "confidence": "critical"
                }
              ]
            }
            """;

        var result = LlmResultParser.Parse(rawResponse);

        result.Success.Should().BeTrue();
        result.Value!.Comments.Should().ContainSingle()
            .Which.Confidence.Should().Be(Confidence.High);
    }

    [Fact]
    public void MalformedJsonReturnsFailureWithError()
    {
        const string rawResponse = """
            {
              "summary": "broken",
              "comments": [
            }
            """;

        var result = LlmResultParser.Parse(rawResponse);

        result.Success.Should().BeFalse();
        result.Value.Should().BeNull();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CommentCapAtOneHundredIsEnforced()
    {
        var comments = string.Join(
            ",\n",
            Enumerable.Range(1, 101).Select(index => $$"""
                {
                  "path": "src/File{{index}}.cs",
                  "line": {{index}},
                  "body": "Comment {{index}}."
                }
                """));
        var rawResponse = $$"""
            {
              "summary": "Many comments.",
              "comments": [
                {{comments}}
              ]
            }
            """;

        var result = LlmResultParser.Parse(rawResponse);

        result.Success.Should().BeTrue();
        result.Value!.Comments.Should().HaveCount(100);
        result.Value.Comments[^1].Path.Should().Be("src/File100.cs");
    }
}
