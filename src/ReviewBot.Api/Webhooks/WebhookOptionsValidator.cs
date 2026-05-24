using Microsoft.Extensions.Options;

namespace ReviewBot.Api.Webhooks;

public sealed class WebhookOptionsValidator : IValidateOptions<WebhookOptions>
{
    public ValidateOptionsResult Validate(string? name, WebhookOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Secret))
        {
            failures.Add("Webhook:Secret must be provided.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
