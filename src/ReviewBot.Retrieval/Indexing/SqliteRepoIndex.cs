using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ReviewBot.Retrieval.Indexing;

public sealed class SqliteRepoIndex : IRepoIndex
{
    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".idea", ".vs", "bin", "obj", "node_modules"
    };

    private readonly string connectionString;
    private readonly IReadOnlyList<IRepoSymbolParser> parsers;
    private readonly TimeProvider clock;

    public SqliteRepoIndex(
        string connectionString,
        IReadOnlyList<IRepoSymbolParser>? parsers = null,
        TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        this.connectionString = connectionString;
        this.parsers = parsers is { Count: > 0 } ? parsers : [new CSharpRepoSymbolParser()];
        this.clock = clock ?? TimeProvider.System;
    }

    public static SqliteRepoIndex CreateForCacheDirectory(
        string indexCacheDir,
        IReadOnlyList<IRepoSymbolParser>? parsers = null,
        TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexCacheDir);
        Directory.CreateDirectory(indexCacheDir);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(indexCacheDir, "symbols.sqlite")
        };

        return new SqliteRepoIndex(builder.ConnectionString, parsers, clock);
    }

    public async Task IndexAsync(RepoIndexRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateKey(new RepoIndexKey(request.Owner, request.Repo, request.Sha));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryRoot);

        var root = Path.GetFullPath(request.RepositoryRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Repository root does not exist: {root}");
        }

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await DeleteKeyAsync(connection, transaction, request.Owner, request.Repo, request.Sha, ct).ConfigureAwait(false);

        var ignore = new GlobMatcher(request.Ignore ?? []);
        var now = FormatTimestamp(clock.GetUtcNow());
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = NormalizeRelativePath(root, file);
            if (ShouldSkip(relativePath, ignore))
            {
                continue;
            }

            var parser = parsers.FirstOrDefault(candidate => candidate.CanParse(relativePath));
            if (parser is null)
            {
                continue;
            }

            var content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            foreach (var symbol in parser.Parse(relativePath, content))
            {
                await InsertSymbolAsync(
                    connection,
                    transaction,
                    request.Owner,
                    request.Repo,
                    request.Sha,
                    symbol,
                    now,
                    ct).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RepoSymbol>> FindAsync(
        RepoIndexKey key,
        string name,
        RepoSymbolKind? kind = null,
        CancellationToken ct = default)
    {
        ValidateKey(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);

        await TouchKeyAsync(connection, key, ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = kind is null
            ? """
              SELECT name, kind, role, path, line, signature
              FROM repo_symbols
              WHERE owner = $owner AND repo = $repo AND sha = $sha AND name = $name
              ORDER BY role ASC, path ASC, line ASC
              """
            : """
              SELECT name, kind, role, path, line, signature
              FROM repo_symbols
              WHERE owner = $owner AND repo = $repo AND sha = $sha AND name = $name AND kind = $kind
              ORDER BY role ASC, path ASC, line ASC
              """;

        AddKeyParameters(command, key.Owner, key.Repo, key.Sha);
        command.Parameters.AddWithValue("$name", name);
        if (kind is not null)
        {
            command.Parameters.AddWithValue("$kind", (int)kind.Value);
        }

        var results = new List<RepoSymbol>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new RepoSymbol(
                reader.GetString(0),
                (RepoSymbolKind)reader.GetInt32(1),
                (RepoSymbolRole)reader.GetInt32(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return results;
    }

    public async Task<int> DeleteUnusedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM repo_symbols WHERE last_accessed_at < $cutoff";
        command.Parameters.AddWithValue("$cutoff", FormatTimestamp(cutoff));
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS repo_symbols (
                owner TEXT NOT NULL,
                repo TEXT NOT NULL,
                sha TEXT NOT NULL,
                name TEXT NOT NULL,
                kind INTEGER NOT NULL,
                role INTEGER NOT NULL,
                path TEXT NOT NULL,
                line INTEGER NOT NULL,
                signature TEXT NULL,
                indexed_at TEXT NOT NULL,
                last_accessed_at TEXT NOT NULL,
                PRIMARY KEY (owner, repo, sha, name, kind, role, path, line)
            );

            CREATE INDEX IF NOT EXISTS ix_repo_symbols_lookup
                ON repo_symbols (owner, repo, sha, name, kind);

            CREATE INDEX IF NOT EXISTS ix_repo_symbols_last_accessed
                ON repo_symbols (last_accessed_at);
            """;

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task DeleteKeyAsync(
        SqliteConnection connection,
        IDbTransaction transaction,
        string owner,
        string repo,
        string sha,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "DELETE FROM repo_symbols WHERE owner = $owner AND repo = $repo AND sha = $sha";
        AddKeyParameters(command, owner, repo, sha);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task InsertSymbolAsync(
        SqliteConnection connection,
        IDbTransaction transaction,
        string owner,
        string repo,
        string sha,
        RepoSymbol symbol,
        string now,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            INSERT OR REPLACE INTO repo_symbols (
                owner, repo, sha, name, kind, role, path, line, signature, indexed_at, last_accessed_at
            )
            VALUES (
                $owner, $repo, $sha, $name, $kind, $role, $path, $line, $signature, $indexedAt, $lastAccessedAt
            )
            """;

        AddKeyParameters(command, owner, repo, sha);
        command.Parameters.AddWithValue("$name", symbol.Name);
        command.Parameters.AddWithValue("$kind", (int)symbol.Kind);
        command.Parameters.AddWithValue("$role", (int)symbol.Role);
        command.Parameters.AddWithValue("$path", symbol.Path);
        command.Parameters.AddWithValue("$line", symbol.Line);
        command.Parameters.AddWithValue("$signature", symbol.Signature is null ? DBNull.Value : symbol.Signature);
        command.Parameters.AddWithValue("$indexedAt", now);
        command.Parameters.AddWithValue("$lastAccessedAt", now);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task TouchKeyAsync(SqliteConnection connection, RepoIndexKey key, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE repo_symbols
            SET last_accessed_at = $lastAccessedAt
            WHERE owner = $owner AND repo = $repo AND sha = $sha
            """;

        AddKeyParameters(command, key.Owner, key.Repo, key.Sha);
        command.Parameters.AddWithValue("$lastAccessedAt", FormatTimestamp(clock.GetUtcNow()));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void AddKeyParameters(SqliteCommand command, string owner, string repo, string sha)
    {
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$repo", repo);
        command.Parameters.AddWithValue("$sha", sha);
    }

    private static void ValidateKey(RepoIndexKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Sha);
    }

    private static bool ShouldSkip(string relativePath, GlobMatcher ignore)
    {
        if (ignore.IsMatch(relativePath))
        {
            return true;
        }

        return relativePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => SkippedDirectoryNames.Contains(part));
    }

    private static string NormalizeRelativePath(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
