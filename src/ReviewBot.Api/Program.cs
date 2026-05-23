using ReviewBot.Api.Webhooks;
using ReviewBot.Core.Jobs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddOptions<WebhookOptions>()
    .Bind(builder.Configuration.GetSection(WebhookOptions.SectionName));
builder.Services.AddChannelReviewJobQueue();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapWebhookEndpoint();

app.Run();

public partial class Program;
