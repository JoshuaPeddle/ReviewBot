namespace ReviewBot.Api.Auth;

public sealed class JwtTokenValidator
{
    private readonly string expectedAudience;

    public JwtTokenValidator(string expectedAudience)
    {
        this.expectedAudience = expectedAudience;
    }

    public bool IsValid(Token token, DateTimeOffset now)
    {
        if (token is null) return false;
        return token.ExpiresAt > now && token.Audience == expectedAudience;
    }
}

public sealed record Token(DateTimeOffset ExpiresAt, string Audience);
