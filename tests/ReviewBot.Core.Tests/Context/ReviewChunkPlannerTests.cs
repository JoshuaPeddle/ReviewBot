using FluentAssertions;
using ReviewBot.Core.Context;
using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Tests.Context;

public class ReviewChunkPlannerTests
{
    [Fact]
    public void PlanChunksReturnsSingleChunkForSingleFileThatFits()
    {
        var planner = new ReviewChunkPlanner(new FixedTokenEstimator(10));

        var chunks = planner.PlanChunks([CreateFile("src/App.cs")], 100, 0.85, 10, 100);

        chunks.Should().ContainSingle();
        chunks[0].Index.Should().Be(1);
        chunks[0].TotalChunks.Should().Be(1);
        chunks[0].Files.Select(file => file.Path).Should().Equal("src/App.cs");
    }

    [Fact]
    public void PlanChunksPacksFilesThatFitInOneChunk()
    {
        var planner = new ReviewChunkPlanner(new FixedTokenEstimator(10));

        var chunks = planner.PlanChunks(
            [CreateFile("src/B.cs"), CreateFile("src/A.cs"), CreateFile("tests/AppTests.cs")],
            contentBudgetTokens: 100,
            headroom: 0.85,
            maxChunks: 10,
            maxPatchLines: 100);

        chunks.Should().ContainSingle();
        chunks[0].Files.Select(file => file.Path)
            .Should().Equal("src/A.cs", "src/B.cs", "tests/AppTests.cs");
    }

    [Fact]
    public void PlanChunksSplitsFilesIntoTwoChunksWhenNeeded()
    {
        var planner = new ReviewChunkPlanner(new FixedTokenEstimator(30));

        var chunks = planner.PlanChunks(
            [CreateFile("src/A.cs"), CreateFile("src/B.cs"), CreateFile("src/C.cs")],
            contentBudgetTokens: 100,
            headroom: 0.85,
            maxChunks: 10,
            maxPatchLines: 100);

        chunks.Should().HaveCount(2);
        chunks[0].Files.Select(file => file.Path).Should().Equal("src/A.cs", "src/B.cs");
        chunks[1].Files.Select(file => file.Path).Should().Equal("src/C.cs");
        chunks.Select(chunk => chunk.TotalChunks).Should().OnlyContain(total => total == 2);
    }

    [Fact]
    public void PlanChunksKeepsOversizedSingleFileInOneChunk()
    {
        var planner = new ReviewChunkPlanner(new FixedTokenEstimator(500));

        var chunks = planner.PlanChunks([CreateFile("src/Huge.cs")], 100, 0.85, 10, 100);

        chunks.Should().ContainSingle();
        chunks[0].Files.Select(file => file.Path).Should().Equal("src/Huge.cs");
        chunks[0].EstimatedTokens.Should().Be(500);
    }

    [Fact]
    public void PlanChunksHonorsMaxChunksCap()
    {
        var planner = new ReviewChunkPlanner(new FixedTokenEstimator(50));

        var chunks = planner.PlanChunks(
            [CreateFile("src/A.cs"), CreateFile("src/B.cs"), CreateFile("src/C.cs")],
            contentBudgetTokens: 100,
            headroom: 0.5,
            maxChunks: 2,
            maxPatchLines: 100);

        chunks.Should().HaveCount(2);
        chunks.SelectMany(chunk => chunk.Files).Select(file => file.Path)
            .Should().Equal("src/A.cs", "src/B.cs");
        chunks.Select(chunk => chunk.TotalChunks).Should().OnlyContain(total => total == 2);
    }

    private static FileChange CreateFile(string path) =>
        new(path, "@@ -1 +1 @@\n+line", new HashSet<int> { 1 }, 1, 0, FileChangeStatus.Modified);

    private sealed class FixedTokenEstimator(int tokens) : IPromptTokenEstimator
    {
        public int EstimateTokens(string? text) => string.IsNullOrEmpty(text) ? 0 : tokens;
    }
}
