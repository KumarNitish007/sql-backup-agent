using System.Net.Http.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using SqlBackupAgent.Core.Catalog;

namespace SqlBackupAgent.Core.Notifications;

public sealed record JobReport(string Job, string Kind, IReadOnlyList<RunRecord> Runs)
{
    public bool Failed => Runs.Any(r => r.Status == RunStatus.Failed);

    public string Title => $"{(Failed ? "FAILED" : "OK")}: {Kind} job '{Job}' ({Runs.Count(r => r.Status == RunStatus.Succeeded)}/{Runs.Count} databases succeeded)";

    public string Body => string.Join('\n', Runs.Select(r => $"- {r.Database}: {r.Status}{(r.Message is null ? "" : " - " + r.Message)}"));
}

/// <summary>Sends job reports to a webhook (Slack/Teams-compatible "text" payload) and/or email. Failures to notify are logged, never thrown.</summary>
public sealed class Notifier(NotificationOptions options, HttpClient http, ILogger<Notifier> logger)
{
    public async Task NotifyAsync(JobReport report, CancellationToken ct)
    {
        if (report.Failed ? !options.OnFailure : !options.OnSuccess) return;

        if (!string.IsNullOrWhiteSpace(options.WebhookUrl))
        {
            try
            {
                var response = await http.PostAsJsonAsync(options.WebhookUrl, new { text = $"*{report.Title}*\n{report.Body}", title = report.Title, failed = report.Failed, job = report.Job }, ct);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Webhook notification failed");
            }
        }

        if (options.Email is { Host.Length: > 0, To.Count: > 0 } email)
        {
            try
            {
                var message = new MimeMessage { Subject = report.Title, Body = new TextPart("plain") { Text = report.Body } };
                message.From.Add(MailboxAddress.Parse(email.From));
                foreach (var to in email.To) message.To.Add(MailboxAddress.Parse(to));

                using var smtp = new SmtpClient();
                await smtp.ConnectAsync(email.Host, email.Port, email.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, ct);
                if (!string.IsNullOrEmpty(email.UserName)) await smtp.AuthenticateAsync(email.UserName, email.Password ?? "", ct);
                await smtp.SendAsync(message, ct);
                await smtp.DisconnectAsync(true, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Email notification failed");
            }
        }
    }
}
