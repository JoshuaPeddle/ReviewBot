namespace ReviewBot.Api.Notifications;

public sealed class SmtpTransport
{
    private readonly string host;
    private readonly int port;

    public SmtpTransport(string host, int port)
    {
        this.host = host;
        this.port = port;
    }

    public async Task SendAsync(string recipient, string body, CancellationToken ct)
    {
        using var client = new System.Net.Mail.SmtpClient(host, port);
        using var message = new System.Net.Mail.MailMessage("reviewbot@localhost", recipient, "ReviewBot review", body);
        await client.SendMailAsync(message, ct).ConfigureAwait(false);
    }
}
