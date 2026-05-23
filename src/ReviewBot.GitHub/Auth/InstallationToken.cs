namespace ReviewBot.GitHub.Auth;

public sealed record InstallationToken(string Token, DateTimeOffset ExpiresAt);
