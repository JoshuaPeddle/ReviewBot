using FluentAssertions;
using Microsoft.Data.Sqlite;
using ReviewBot.Retrieval.Indexing;

namespace ReviewBot.Retrieval.Tests.Indexing;

public sealed class SqliteRepoIndexTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "reviewbot-retrieval-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task IndexAsyncStoresCSharpDefinitionsAndUsagesBySha()
    {
        var repoRoot = CreateDirectory("repo");
        WriteFile(
            repoRoot,
            "src/Contracts/IUserRepository.cs",
            """
            namespace Demo.Contracts;

            public interface IUserRepository
            {
                Task<User?> GetAsync(int id);
            }
            """);
        WriteFile(
            repoRoot,
            "src/Services/UserService.cs",
            """
            using Demo.Contracts;

            public sealed class UserService
            {
                private readonly IUserRepository repository;

                public UserService(IUserRepository repository)
                {
                    this.repository = repository;
                }

                public Task<User?> FindAsync(int id) => repository.GetAsync(id);
            }
            """);

        var index = CreateIndex();
        var key = new RepoIndexKey("octo", "reviewbot", "abc123");

        await index.IndexAsync(new RepoIndexRequest(key.Owner, key.Repo, key.Sha, repoRoot));

        var userRepository = await index.FindAsync(key, "IUserRepository", RepoSymbolKind.Type);
        userRepository.Should().Contain(symbol => symbol.Role == RepoSymbolRole.Definition &&
                                                  symbol.Path == "src/Contracts/IUserRepository.cs" &&
                                                  symbol.Line == 3);
        userRepository.Should().Contain(symbol => symbol.Role == RepoSymbolRole.Usage &&
                                                  symbol.Path == "src/Services/UserService.cs" &&
                                                  symbol.Line == 5);

        var getAsync = await index.FindAsync(key, "GetAsync", RepoSymbolKind.Method);
        getAsync.Should().Contain(symbol => symbol.Role == RepoSymbolRole.Definition &&
                                            symbol.Path == "src/Contracts/IUserRepository.cs" &&
                                            symbol.Line == 5);
        getAsync.Should().Contain(symbol => symbol.Role == RepoSymbolRole.Usage &&
                                            symbol.Path == "src/Services/UserService.cs" &&
                                            symbol.Line == 12);
    }

    [Fact]
    public async Task IndexAsyncSkipsIgnoredPathsAndDefaultBuildDirectories()
    {
        var repoRoot = CreateDirectory("ignored-repo");
        WriteFile(repoRoot, "src/App.cs", "public sealed class App { }\n");
        WriteFile(repoRoot, "src/Generated/GeneratedType.cs", "public sealed class GeneratedType { }\n");
        WriteFile(repoRoot, "bin/Debug/BinaryType.cs", "public sealed class BinaryType { }\n");

        var index = CreateIndex();
        var key = new RepoIndexKey("octo", "reviewbot", "ignored");

        await index.IndexAsync(new RepoIndexRequest(
            key.Owner,
            key.Repo,
            key.Sha,
            repoRoot,
            ["src/Generated/**"]));

        (await index.FindAsync(key, "App", RepoSymbolKind.Type)).Should().ContainSingle();
        (await index.FindAsync(key, "GeneratedType", RepoSymbolKind.Type)).Should().BeEmpty();
        (await index.FindAsync(key, "BinaryType", RepoSymbolKind.Type)).Should().BeEmpty();
    }

    [Fact]
    public async Task IndexAsyncReplacesExistingRowsForSameKeyOnly()
    {
        var repoRoot = CreateDirectory("replace-repo");
        WriteFile(repoRoot, "src/App.cs", "public sealed class FirstType { }\n");

        var index = CreateIndex();
        var key = new RepoIndexKey("octo", "reviewbot", "replace");
        var otherSha = key with { Sha = "other" };

        await index.IndexAsync(new RepoIndexRequest(key.Owner, key.Repo, key.Sha, repoRoot));
        await index.IndexAsync(new RepoIndexRequest(otherSha.Owner, otherSha.Repo, otherSha.Sha, repoRoot));

        WriteFile(repoRoot, "src/App.cs", "public sealed class SecondType { }\n");

        await index.IndexAsync(new RepoIndexRequest(key.Owner, key.Repo, key.Sha, repoRoot));

        (await index.FindAsync(key, "FirstType", RepoSymbolKind.Type)).Should().BeEmpty();
        (await index.FindAsync(key, "SecondType", RepoSymbolKind.Type)).Should().ContainSingle();
        (await index.FindAsync(otherSha, "FirstType", RepoSymbolKind.Type)).Should().ContainSingle();
    }

    [Fact]
    public async Task IndexAsyncMarksShaIndexedEvenWhenNoSymbolsAreStored()
    {
        var repoRoot = CreateDirectory("empty-index-repo");
        WriteFile(repoRoot, "README.md", "# docs only\n");

        var index = CreateIndex();
        var key = new RepoIndexKey("octo", "reviewbot", "docs-only");

        await index.IndexAsync(new RepoIndexRequest(key.Owner, key.Repo, key.Sha, repoRoot));

        (await index.IsIndexedAsync(key)).Should().BeTrue();
        (await index.FindAsync(key, "Anything", RepoSymbolKind.Type)).Should().BeEmpty();
    }

    [Fact]
    public async Task IndexChangesAsyncCopiesUnchangedSymbolsAndReparsesChangedPaths()
    {
        var baseRoot = CreateDirectory("base-repo");
        WriteFile(baseRoot, "src/Stable.cs", "public sealed class StableType { }\n");
        WriteFile(baseRoot, "src/Changed.cs", "public sealed class OldChangedType { }\n");
        WriteFile(baseRoot, "src/Deleted.cs", "public sealed class DeletedType { }\n");

        var headRoot = CreateDirectory("head-repo");
        WriteFile(headRoot, "src/Stable.cs", "public sealed class StableType { }\n");
        WriteFile(headRoot, "src/Changed.cs", "public sealed class NewChangedType { }\n");
        WriteFile(headRoot, "src/Added.cs", "public sealed class AddedType { }\n");

        var index = CreateIndex();
        var baseKey = new RepoIndexKey("octo", "reviewbot", "base");
        var headKey = new RepoIndexKey("octo", "reviewbot", "head");

        await index.IndexAsync(new RepoIndexRequest(baseKey.Owner, baseKey.Repo, baseKey.Sha, baseRoot));
        await index.IndexChangesAsync(
            new RepoIndexRequest(headKey.Owner, headKey.Repo, headKey.Sha, headRoot),
            baseKey,
            ["src/Changed.cs", "src/Added.cs", "src/Deleted.cs"]);

        (await index.IsIndexedAsync(headKey)).Should().BeTrue();
        (await index.FindAsync(headKey, "StableType", RepoSymbolKind.Type)).Should().ContainSingle();
        (await index.FindAsync(headKey, "OldChangedType", RepoSymbolKind.Type)).Should().BeEmpty();
        (await index.FindAsync(headKey, "NewChangedType", RepoSymbolKind.Type)).Should().ContainSingle();
        (await index.FindAsync(headKey, "AddedType", RepoSymbolKind.Type)).Should().ContainSingle();
        (await index.FindAsync(headKey, "DeletedType", RepoSymbolKind.Type)).Should().BeEmpty();
        (await index.FindAsync(baseKey, "OldChangedType", RepoSymbolKind.Type)).Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteUnusedBeforeAsyncEvictsOldRows()
    {
        var repoRoot = CreateDirectory("evict-repo");
        WriteFile(repoRoot, "src/App.cs", "public sealed class EvictableType { }\n");

        var index = CreateIndex();
        var key = new RepoIndexKey("octo", "reviewbot", "evict");

        await index.IndexAsync(new RepoIndexRequest(key.Owner, key.Repo, key.Sha, repoRoot));

        var deleted = await index.DeleteUnusedBeforeAsync(DateTimeOffset.UtcNow.AddDays(1));

        deleted.Should().BeGreaterThan(0);
        (await index.FindAsync(key, "EvictableType", RepoSymbolKind.Type)).Should().BeEmpty();
    }

    [Fact]
    public void SqliteRepoIndexFactoryTracksNormalizedCacheDirectoriesOnce()
    {
        var factory = new SqliteRepoIndexFactory([new CSharpRepoSymbolParser()], TimeProvider.System);
        var cacheDirectory = Path.Combine(tempRoot, "cache", "..", "cache");
        var normalized = Path.GetFullPath(cacheDirectory);

        factory.Create(cacheDirectory);
        factory.Create(normalized);

        factory.GetKnownCacheDirectories().Should().Equal(normalized);
        Directory.Exists(normalized).Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private SqliteRepoIndex CreateIndex()
    {
        Directory.CreateDirectory(tempRoot);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(tempRoot, Guid.NewGuid().ToString("N") + ".sqlite")
        };

        return new SqliteRepoIndex(builder.ConnectionString);
    }

    private string CreateDirectory(string name)
    {
        var path = Path.Combine(tempRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
