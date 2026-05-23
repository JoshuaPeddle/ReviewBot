using Microsoft.Extensions.Options;

namespace ReviewBot.Api.Options;

public sealed class WorkerOptionsValidator : IValidateOptions<WorkerOptions>
{
    public ValidateOptionsResult Validate(string? name, WorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Concurrency < 1
            ? ValidateOptionsResult.Fail("Worker:Concurrency must be at least 1.")
            : ValidateOptionsResult.Success;
    }
}
