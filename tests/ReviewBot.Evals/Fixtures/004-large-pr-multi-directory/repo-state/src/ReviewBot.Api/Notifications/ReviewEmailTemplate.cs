namespace ReviewBot.Api.Notifications;

public static class ReviewEmailTemplate
{
    public static string BuildSubject(string repo, int pullRequestNumber) =>
        $"ReviewBot: {repo} PR #{pullRequestNumber}";
}
