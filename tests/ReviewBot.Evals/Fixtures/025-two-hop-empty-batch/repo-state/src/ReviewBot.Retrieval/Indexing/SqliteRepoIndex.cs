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

        await UpsertKeyAsync(connection, transaction, request.Owner, request.Repo, request.Sha, now, ct)
            .ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task IndexChangesAsync(
        RepoIndexRequest request,
        RepoIndexKey baseKey,
        IReadOnlyCollection<string> changedPaths,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(baseKey);
        ArgumentNullException.ThrowIfNull(changedPaths);
        ValidateKey(new RepoIndexKey(request.Owner, request.Repo, request.Sha));
        ValidateKey(baseKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryRoot);

        var root = Path.GetFullPath(request.RepositoryRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Repository root does not exist: {root}");
        }

        var normalizedChangedPaths = changedPaths
            .Select(NormalizeChangedPath)
            .Where(path => path is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (!await KeyExistsAsync(connection, transaction, baseKey, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Cannot incrementally index {request.Owner}/{request.Repo}@{request.Sha} because base SHA {baseKey.Sha} is not indexed.");
        }

        await DeleteKeyAsync(connection, transaction, request.Owner, request.Repo, request.Sha, ct).ConfigureAwait(false);

        var ignore = new GlobMatcher(request.Ignore ?? []);
        var now = FormatTimestamp(clock.GetUtcNow());
        var baseSymbols = await ReadSymbolsForKeyAsync(connection, transaction, baseKey, ct).ConfigureAwait(false);
        foreach (var symbol in baseSymbols)
        {
            ct.ThrowIfCancellationRequested();
            if (normalizedChangedPaths.Contains(symbol.Path) || ShouldSkip(symbol.Path, ignore))
            {
                continue;
            }

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

        foreach (var relativePath in normalizedChangedPaths.Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (ShouldSkip(relativePath, ignore))
            {
                continue;
            }

            var parser = parsers.FirstOrDefault(candidate => candidate.CanParse(relativePath));
            if (parser is null)
            {
                continue;
            }

            var fullPath = GetSafeFullPath(root, relativePath);
            if (fullPath is null || !File.Exists(fullPath))
            {
                continue;
            }

            var content = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
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

        await UpsertKeyAsync(connection, transaction, request.Owner, request.Repo, request.Sha, now, ct)
            .ConfigureAwait(false);
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
              SELECT name, kind, role, path, line, signature, body_text, body_start, body_end
              FROM repo_symbols
              WHERE owner = $owner AND repo = $repo AND sha = $sha AND name = $name
              ORDER BY role ASC, path ASC, line ASC
              """
            : """
              SELECT name, kind, role, path, line, signature, body_text, body_start, body_end
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
            results.Add(ReadSymbol(reader));
        }

        return results;
    }

    private static RepoSymbol ReadSymbol(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            (RepoSymbolKind)reader.GetInt32(1),
            (RepoSymbolRole)reader.GetInt32(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8));

    public async Task<bool> IsIndexedAsync(RepoIndexKey key, CancellationToken ct = default)
    {
        ValidateKey(key);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM repo_index_keys
            WHERE owner = $owner AND repo = $repo AND sha = $sha
            LIMIT 1
            """;
        AddKeyParameters(command, key.Owner, key.Repo, key.Sha);

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null;
    }

    public async Task<int> DeleteUnusedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);

        var cutoffValue = FormatTimestamp(cutoff);
        var deleted = 0;

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM repo_symbols
            WHERE last_accessed_at < $cutoff
               OR EXISTS (
                   SELECT 1
                   FROM repo_index_keys keys
                   WHERE keys.owner = repo_symbols.owner
                     AND keys.repo = repo_symbols.repo
                     AND keys.sha = repo_symbols.sha
                     AND keys.last_accessed_at < $cutoff
               )
            """;
        command.Parameters.AddWithValue("$cutoff", cutoffValue);
        deleted += await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var keyCommand = connection.CreateCommand();
        keyCommand.CommandText = "DELETE FROM repo_index_keys WHERE last_accessed_at < $cutoff";
        keyCommand.Parameters.AddWithValue("$cutoff", cutoffValue);
        deleted += await keyCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return deleted;
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
                body_text TEXT NULL,
                body_start INTEGER NULL,
                body_end INTEGER NULL,
                indexed_at TEXT NOT NULL,
                last_accessed_at TEXT NOT NULL,
                PRIMARY KEY (owner, repo, sha, name, kind, role, path, line)
            );

            CREATE TABLE IF NOT EXISTS repo_index_keys (
                owner TEXT NOT NULL,
                repo TEXT NOT NULL,
                sha TEXT NOT NULL,
                indexed_at TEXT NOT NULL,
                last_accessed_at TEXT NOT NULL,
                PRIMARY KEY (owner, repo, sha)
            );

            CREATE INDEX IF NOT EXISTS ix_repo_symbols_lookup
                ON repo_symbols (owner, repo, sha, name, kind);

            CREATE INDEX IF NOT EXISTS ix_repo_symbols_last_accessed
                ON repo_symbols (last_accessed_at);
            """;

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Additive migration for indices created before the body fields existed.
        // SQLite rejects duplicate columns with error 1; swallow that one only.
        await TryAddColumnAsync(connection, "body_text", "TEXT NULL", ct).ConfigureAwait(false);
        await TryAddColumnAsync(connection, "body_start", "INTEGER NULL", ct).ConfigureAwait(false);
        await TryAddColumnAsync(connection, "body_end", "INTEGER NULL", ct).ConfigureAwait(false);
    }

    private static async Task TryAddColumnAsync(
        SqliteConnection connection,
        string columnName,
        string columnDefinition,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE repo_symbols ADD COLUMN {columnName} {columnDefinition}";
        try
        {
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // Column already exists. No-op.
        }
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

        await using var keyCommand = connection.CreateCommand();
        keyCommand.Transaction = (SqliteTransaction)transaction;
        keyCommand.CommandText = "DELETE FROM repo_index_keys WHERE owner = $owner AND repo = $repo AND sha = $sha";
        AddKeyParameters(keyCommand, owner, repo, sha);
        await keyCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
                owner, repo, sha, name, kind, role, path, line, signature, body_text, body_start, body_end, indexed_at, last_accessed_at
            )
            VALUES (
                $owner, $repo, $sha, $name, $kind, $role, $path, $line, $signature, $bodyText, $bodyStart, $bodyEnd, $indexedAt, $lastAccessedAt
            )
            """;

        AddKeyParameters(command, owner, repo, sha);
        command.Parameters.AddWithValue("$name", symbol.Name);
        command.Parameters.AddWithValue("$kind", (int)symbol.Kind);
        command.Parameters.AddWithValue("$role", (int)symbol.Role);
        command.Parameters.AddWithValue("$path", symbol.Path);
        command.Parameters.AddWithValue("$line", symbol.Line);
        command.Parameters.AddWithValue("$signature", symbol.Signature is null ? DBNull.Value : symbol.Signature);
        command.Parameters.AddWithValue("$bodyText", symbol.Body is null ? DBNull.Value : symbol.Body);
        command.Parameters.AddWithValue("$bodyStart", symbol.BodyStartLine is null ? DBNull.Value : symbol.BodyStartLine);
        command.Parameters.AddWithValue("$bodyEnd", symbol.BodyEndLine is null ? DBNull.Value : symbol.BodyEndLine);
        command.Parameters.AddWithValue("$indexedAt", now);
        command.Parameters.AddWithValue("$lastAccessedAt", now);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task UpsertKeyAsync(
        SqliteConnection connection,
        IDbTransaction transaction,
        string owner,
        string repo,
        string sha,
        string now,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            INSERT OR REPLACE INTO repo_index_keys (
                owner, repo, sha, indexed_at, last_accessed_at
            )
            VALUES (
                $owner, $repo, $sha, $indexedAt, $lastAccessedAt
            )
            """;

        AddKeyParameters(command, owner, repo, sha);
        command.Parameters.AddWithValue("$indexedAt", now);
        command.Parameters.AddWithValue("$lastAccessedAt", now);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> KeyExistsAsync(
        SqliteConnection connection,
        IDbTransaction transaction,
        RepoIndexKey key,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT 1
            FROM repo_index_keys
            WHERE owner = $owner AND repo = $repo AND sha = $sha
            LIMIT 1
            """;

        AddKeyParameters(command, key.Owner, key.Repo, key.Sha);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null;
    }

    private static async Task<IReadOnlyList<RepoSymbol>> ReadSymbolsForKeyAsync(
        SqliteConnection connection,
        IDbTransaction transaction,
        RepoIndexKey key,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT name, kind, role, path, line, signature, body_text, body_start, body_end
            FROM repo_symbols
            WHERE owner = $owner AND repo = $repo AND sha = $sha
            ORDER BY path ASC, line ASC, name ASC, kind ASC, role ASC
            """;

        AddKeyParameters(command, key.Owner, key.Repo, key.Sha);

        var symbols = new List<RepoSymbol>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            symbols.Add(ReadSymbol(reader));
        }

        return symbols;
    }

    private async Task TouchKeyAsync(SqliteConnection connection, RepoIndexKey key, CancellationToken ct)
    {
        var now = FormatTimestamp(clock.GetUtcNow());

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE repo_symbols
            SET last_accessed_at = $lastAccessedAt
            WHERE owner = $owner AND repo = $repo AND sha = $sha
            """;

        AddKeyParameters(command, key.Owner, key.Repo, key.Sha);
        command.Parameters.AddWithValue("$lastAccessedAt", now);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var keyCommand = connection.CreateCommand();
        keyCommand.CommandText =
            """
            UPDATE repo_index_keys
            SET last_accessed_at = $lastAccessedAt
            WHERE owner = $owner AND repo = $repo AND sha = $sha
            """;

        AddKeyParameters(keyCommand, key.Owner, key.Repo, key.Sha);
        keyCommand.Parameters.AddWithValue("$lastAccessedAt", now);
        await keyCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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

    private static string? NormalizeChangedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return null;
        }

        var normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            return null;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => part is "." or "..") ? null : string.Join('/', parts);
    }

    private static string? GetSafeFullPath(string root, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal) ? fullPath : null;
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
