using FluentAssertions;
using ReviewBot.Core.Context;
using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Tests.Context;

public sealed class FullFileContextSelectorTests
{
    [Fact]
    public void SelectsAnOrdinaryEditedFile()
    {
        var file = CreateFile("src/A.cs", additions: 3, deletions: 4);

        FullFileContextSelector.SelectCandidates([file], fullFileMaxBytes: 65_536)
            .Should().ContainSingle().Which.Path.Should().Be("src/A.cs");
    }

    [Fact]
    public void SkipsRemovedFiles()
    {
        var file = CreateFile("src/Gone.cs", additions: 0, deletions: 10, status: FileChangeStatus.Removed);

        FullFileContextSelector.SelectCandidates([file], fullFileMaxBytes: 65_536).Should().BeEmpty();
    }

    [Fact]
    public void SkipsMostlyNewFilesBecauseTheDiffAlreadyShowsThem()
    {
        // 95% additions: the diff already contains essentially the whole file, so sending
        // it again is duplicated content for no extra context.
        var file = CreateFile("src/New.cs", additions: 95, deletions: 5);

        FullFileContextSelector.IsMostlyNewFile(file).Should().BeTrue();
        FullFileContextSelector.SelectCandidates([file], fullFileMaxBytes: 65_536).Should().BeEmpty();
    }

    [Fact]
    public void KeepsAFileExactlyAtTheAdditionRatioThreshold()
    {
        // The rule is strictly greater-than, so a file right on the line still qualifies.
        var file = CreateFile("src/Edge.cs", additions: 90, deletions: 10);

        FullFileContextSelector.IsMostlyNewFile(file).Should().BeFalse();
        FullFileContextSelector.SelectCandidates([file], fullFileMaxBytes: 65_536).Should().ContainSingle();
    }

    [Fact]
    public void TreatsAFileWithNoCountedChangesAsEligible()
    {
        // Guards the divide-by-zero path rather than letting it decide by accident.
        var file = CreateFile("src/Rename.cs", additions: 0, deletions: 0);

        FullFileContextSelector.IsMostlyNewFile(file).Should().BeFalse();
    }

    [Fact]
    public void SkipsFilesWhosePatchExceedsTheByteLimit()
    {
        var file = CreateFile("src/Huge.cs", additions: 5, deletions: 5, patch: new string('x', 200));

        FullFileContextSelector.SelectCandidates([file], fullFileMaxBytes: 100).Should().BeEmpty();
        FullFileContextSelector.SelectCandidates([file], fullFileMaxBytes: 500).Should().ContainSingle();
    }

    [Fact]
    public void MeasuresPatchSizeInBytesNotCharacters()
    {
        // A multi-byte patch must not slip under a byte limit by being counted as chars.
        var file = CreateFile("src/Unicode.cs", additions: 1, deletions: 1, patch: new string('é', 10));

        FullFileContextSelector.EstimatePatchBytes(file).Should().Be(20);
        FullFileContextSelector.SelectCandidates([file], fullFileMaxBytes: 15).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ReturnsNothingWhenFullFileContextIsDisabled(int maxBytes)
    {
        var file = CreateFile("src/A.cs", additions: 1, deletions: 1);

        FullFileContextSelector.SelectCandidates([file], maxBytes).Should().BeEmpty();
    }

    private static FileChange CreateFile(
        string path,
        int additions,
        int deletions,
        FileChangeStatus status = FileChangeStatus.Modified,
        string? patch = null) =>
        new(
            path,
            patch ?? "@@ -1,2 +1,2 @@\n-old\n+new",
            new HashSet<int> { 1, 2 },
            additions,
            deletions,
            status);
}
