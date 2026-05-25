using FluentAssertions;
using ReviewBot.Core.Domain;

namespace ReviewBot.Evals.Tests;

public class EvalFixtureLoaderTests : IDisposable
{
    private readonly string fixtureDirectory = Path.Combine(Path.GetTempPath(), $"reviewbot-eval-{Guid.NewGuid():N}");

    [Fact]
    public void LoadReadsFixtureMetadataDiffAndExpectations()
    {
        WriteValidFixture("""
            must_flag:
              - path: src/Review.cs
                line_range: [10, 12]
                severity_at_least: warning
                topic: null_guard
                must_mention_any: ["null", "guard"]
            must_not_flag:
              - path: src/Generated.cs
                reason: generated file
                severity_above: info
            max_total_comments: 3
            expected_review_state: REQUEST_CHANGES
            """);

        var fixture = new EvalFixtureLoader().Load(fixtureDirectory);

        fixture.DirectoryPath.Should().Be(Path.GetFullPath(fixtureDirectory));
        fixture.Metadata.Name.Should().Be("Null guard regression");
        fixture.DiffPatch.Should().Contain("+ return value.Length;");
        fixture.Expected.MustFlag.Should().ContainSingle().Which.Should().BeEquivalentTo(new MustFlagExpectation(
            "src/Review.cs",
            StartLine: 10,
            EndLine: 12,
            Severity.Warning,
            "null_guard",
            ["null", "guard"]));
        fixture.Expected.MustNotFlag.Should().ContainSingle().Which.Should().Be(new MustNotFlagExpectation(
            "src/Generated.cs",
            "generated file",
            Severity.Info));
        fixture.Expected.MaxTotalComments.Should().Be(3);
        fixture.Expected.ExpectedReviewState.Should().Be("REQUEST_CHANGES");
    }

    [Fact]
    public void LoadRejectsInvalidLineRange()
    {
        WriteValidFixture("""
            must_flag:
              - path: src/Review.cs
                line_range: [12, 10]
                severity_at_least: warning
                topic: null_guard
            """);

        var act = () => new EvalFixtureLoader().Load(fixtureDirectory);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*line_range*positive [start, end]*");
    }

    [Fact]
    public void LoadRejectsInvalidExpectedReviewState()
    {
        WriteValidFixture("""
            expected_review_state: DISMISS
            """);

        var act = () => new EvalFixtureLoader().Load(fixtureDirectory);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*expected_review_state*APPROVE, COMMENT, or REQUEST_CHANGES*");
    }

    public void Dispose()
    {
        if (Directory.Exists(fixtureDirectory))
        {
            Directory.Delete(fixtureDirectory, recursive: true);
        }
    }

    private void WriteValidFixture(string expectedYaml)
    {
        Directory.CreateDirectory(fixtureDirectory);
        File.WriteAllText(Path.Combine(fixtureDirectory, "fixture.yaml"), """
            name: Null guard regression
            category: correctness
            difficulty: medium
            description: |
              A null guard was removed before dereferencing the value.
            """);
        File.WriteAllText(Path.Combine(fixtureDirectory, "diff.patch"), """
            diff --git a/src/Review.cs b/src/Review.cs
            @@ -10,3 +10,3 @@
            - if (value is null) return 0;
            + return value.Length;
            """);
        File.WriteAllText(Path.Combine(fixtureDirectory, "expected.yaml"), expectedYaml);
    }
}
