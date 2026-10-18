using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Ratable.Modules.Businesses.Services;

/// <summary>Bound from the "Email" section. The password comes from the environment (Email__Password), never from a file.</summary>
public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>The address messages come from. For now Ratable's own support address.</summary>
    public string From { get; set; } = "support@coon.one";
    public string FromName { get; set; } = "Ratable";

    /// <summary>SMTP server. Empty means no real mail is sent.</summary>
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>
    /// For development and tests only: write every message as a text file in this folder instead of sending it. Ignored when Host is set.
    /// </summary>
    public string OutboxPath { get; set; } = "";
}

public sealed class EmailUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Sends one plain-text message. The message body may hold a one-time code, so implementations never log it.</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default);
}

/// <summary>Real mail over SMTP, from the configured address (support@coon.one for now).</summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        var o = options.Value;
        try
        {
            using var client = new SmtpClient(o.Host, o.Port) { EnableSsl = o.UseSsl, Credentials = string.IsNullOrEmpty(o.User) ? null : new NetworkCredential(o.User, o.Password) };
            using var message = new MailMessage(new MailAddress(o.From, o.FromName), new MailAddress(to)) { Subject = subject, Body = body, IsBodyHtml = false };
            await client.SendMailAsync(message, ct);
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException)
        {
            throw new EmailUnavailableException("email_send_failed", ex);
        }
    }
}

/// <summary>Writes each message to a file. Development and tests only (Email:OutboxPath).</summary>
public sealed class FileEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        var folder = Path.GetFullPath(options.Value.OutboxPath);
        Directory.CreateDirectory(folder);
        var name = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.txt";
        await File.WriteAllTextAsync(Path.Combine(folder, name), $"To: {to}\nFrom: {options.Value.From}\nSubject: {subject}\n\n{body}\n", ct);
    }
}

/// <summary>Used when no mail is set up. Claims cannot confirm a company email until Email__Host is configured.</summary>
public sealed class UnconfiguredEmailSender : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default) => throw new EmailUnavailableException("email_not_configured");
}
