using System.ComponentModel.DataAnnotations;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using OrderManagement.Application;
namespace OrderManagement.Api.Security;

public class SmtpOptions
{
    [Required] public string Host { get; set; } = "";
    [Range(1, 65535)] public int Port { get; set; } = 587;
    [Required, EmailAddress] public string From { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
}
public class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions options;
    public SmtpEmailSender(IOptions<SmtpOptions> options) => this.options = options.Value;
    public async Task SendAsync(string email, string subject, string link, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(options.From));
        message.To.Add(MailboxAddress.Parse(email));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = $"{subject}\n\n{link}\n\nIf you did not request this, ignore this email." };
        using var client = new SmtpClient { Timeout = 15000 };
        // No protocol logger: SMTP payloads contain private confirmation/reset links.
        // StartTls is mandatory here (not opportunistic StartTlsWhenAvailable). Authenticate
        // and send the recovery link only after the TLS upgrade and normal certificate validation.
        await client.ConnectAsync(options.Host, options.Port, SecureSocketOptions.StartTls, ct);
        if (!string.IsNullOrEmpty(options.Username))
            await client.AuthenticateAsync(options.Username,
                options.Password ?? throw new InvalidOperationException("Configure SMTP password when a username is supplied."), ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}
