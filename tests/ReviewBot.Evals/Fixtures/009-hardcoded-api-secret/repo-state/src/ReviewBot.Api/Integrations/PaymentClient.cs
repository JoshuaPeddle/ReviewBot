using System.Net.Http.Headers;

namespace ReviewBot.Api.Integrations;

public sealed class PaymentClient
{
    private readonly HttpClient http;

    public PaymentClient(HttpClient http)
    {
        this.http = http;
        this.http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "sk_live_8f3a9c1b2d4e5f60718293a4b5c6d7e8");
    }

    public async Task<PaymentReceipt> ChargeAsync(string customerId, decimal amount, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(
            $"/charges/{customerId}",
            new { amount },
            ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PaymentReceipt>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty payment response.");
    }
}

public sealed record PaymentReceipt(string Id, decimal Amount, string Status);
