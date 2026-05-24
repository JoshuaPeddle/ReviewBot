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

        if (string.IsNullOrWhiteSpace(options.PrivateKeyPem) &&
            string.IsNullOrWhiteSpace(options.PrivateKeyPemFile))
        {
            failures.Add("GitHubApp:PrivateKeyPem or GitHubApp:PrivateKeyPemFile must be provided.");
        }
        else if (!string.IsNullOrWhiteSpace(options.PrivateKeyPemFile) &&
                 !File.Exists(options.PrivateKeyPemFile))
        {
            failures.Add($"GitHubApp:PrivateKeyPemFile '{options.PrivateKeyPemFile}' does not exist.");
        }

        if (options.ApiBaseUrl is null || !options.ApiBaseUrl.IsAbsoluteUri)
        {
            failures.Add("GitHubApp:ApiBaseUrl must be an absolute URI.");
        }
        else if (options.ApiBaseUrl.Scheme is not "https" and not "http")
        {
            failures.Add("GitHubApp:ApiBaseUrl must use HTTP or HTTPS.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
