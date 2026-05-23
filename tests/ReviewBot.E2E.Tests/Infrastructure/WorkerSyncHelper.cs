using WireMock.Server;

namespace ReviewBot.E2E.Tests.Infrastructure;

public static class WorkerSyncHelper
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    public static async Task WaitForRequestAsync(
        WireMockServer server,
        Func<string, bool> pathPredicate,
        string method = "POST",
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(pathPredicate);

        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (HasMatchingRequest(server, method, pathPredicate))
            {
                return;
            }

            await Task.Delay(PollInterval, ct);
        }

        var seen = string.Join(
            ", ",
            server.LogEntries.Select(entry =>
                $"{entry.RequestMessage?.Method ?? "<unknown>"} {entry.RequestMessage?.Path ?? "<unknown>"}"));
        throw new TimeoutException(
            $"Timed out waiting for {method} request matching the supplied path predicate. Seen requests: {seen}");
    }

    public static async Task WaitForRequestCountAsync(
        WireMockServer server,
        Func<string, bool> pathPredicate,
        int expectedCount,
        string method = "POST",
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(pathPredicate);
        if (expectedCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedCount), expectedCount, "Expected count cannot be negative.");
        }

        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (CountMatchingRequests(server, method, pathPredicate) >= expectedCount)
            {
                return;
            }

            await Task.Delay(PollInterval, ct);
        }

        var seen = string.Join(
            ", ",
            server.LogEntries.Select(entry =>
                $"{entry.RequestMessage?.Method ?? "<unknown>"} {entry.RequestMessage?.Path ?? "<unknown>"}"));
        throw new TimeoutException(
            $"Timed out waiting for at least {expectedCount} {method} request(s) matching the supplied path predicate. Seen requests: {seen}");
    }

    public static async Task WaitForNoCallAsync(
        WireMockServer server,
        Func<string, bool> pathPredicate,
        string method = "POST",
        TimeSpan? quietPeriod = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(pathPredicate);

        var deadline = DateTimeOffset.UtcNow + (quietPeriod ?? TimeSpan.FromSeconds(2));
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (HasMatchingRequest(server, method, pathPredicate))
            {
                throw new InvalidOperationException($"Unexpected {method} request matched the supplied path predicate.");
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    private static bool HasMatchingRequest(
        WireMockServer server,
        string method,
        Func<string, bool> pathPredicate) =>
        CountMatchingRequests(server, method, pathPredicate) > 0;

    public static int CountMatchingRequests(
        WireMockServer server,
        string method,
        Func<string, bool> pathPredicate) =>
        server.LogEntries.Count(entry =>
            entry.RequestMessage is { Path: not null } request &&
            string.Equals(request.Method, method, StringComparison.OrdinalIgnoreCase) &&
            pathPredicate(request.Path));
}
