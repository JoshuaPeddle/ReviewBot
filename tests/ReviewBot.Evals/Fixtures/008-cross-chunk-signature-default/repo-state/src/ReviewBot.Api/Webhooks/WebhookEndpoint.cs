namespace ReviewBot.Api.Webhooks;

public static class WebhookEndpoint
{
    public static IResult Handle(
        HttpRequest request,
        WebhookSignatureValidator validator,
        WebhookOptions options)
    {
        if (options.RequireSignature && !validator.IsValid(request))
        {
            return Results.Unauthorized();
        }

        return Results.Ok();
    }
}
