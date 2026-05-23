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
        server.LogEntries.Any(entry =>
            entry.RequestMessage is { Path: not null } request &&
            string.Equals(request.Method, method, StringComparison.OrdinalIgnoreCase) &&
            pathPredicate(request.Path));
}
