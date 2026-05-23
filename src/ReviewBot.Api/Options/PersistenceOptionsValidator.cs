using Microsoft.Extensions.Options;

namespace ReviewBot.Api.Options;

public sealed class PersistenceOptionsValidator : IValidateOptions<PersistenceOptions>
{
    public ValidateOptionsResult Validate(string? name, PersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return string.IsNullOrWhiteSpace(options.ConnectionString)
            ? ValidateOptionsResult.Fail("Persistence:ConnectionString must be provided.")
            : ValidateOptionsResult.Success;
    }
}
