using Microsoft.Extensions.Options;
using ReviewBot.GitHub.Auth;

namespace ReviewBot.Api.Options;

public sealed class GitHubAppOptionsValidator : IValidateOptions<GitHubAppOptions>
{
    public ValidateOptionsResult Validate(string? name, GitHubAppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (options.AppId <= 0)
        {
            failures.Add("GitHubApp:AppId must be greater than 0.");
        }

        if (string.IsNullOrWhiteSpace(options.PrivateKeyPem))
        {
            failures.Add("GitHubApp:PrivateKeyPem must be provided.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
