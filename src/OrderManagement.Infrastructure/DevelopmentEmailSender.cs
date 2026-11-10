using System.Text.Json;
using OrderManagement.Application;
namespace OrderManagement.Infrastructure;
// Deliberately writes links only to an ignored private development mailbox, never normal logs.
public class DevelopmentEmailSender : IEmailSender
{
    private readonly string directory;
    public DevelopmentEmailSender(string directory) => this.directory = directory;
    public Task SendAsync(string email, string subject, string link, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        return File.WriteAllTextAsync(Path.Combine(directory, Guid.NewGuid() + ".json"), JsonSerializer.Serialize(new { email, subject, link }), ct);
    }
}
public class UnconfiguredEmailSender : IEmailSender
{
    public Task SendAsync(string email, string subject, string link, CancellationToken ct) => throw new InvalidOperationException("Configure a production email provider.");
}
public class DisabledEmailSender : IEmailSender
{
    public Task SendAsync(string email, string subject, string link, CancellationToken ct)
        => throw new BusinessException("Email features are temporarily unavailable.", 503);
}
