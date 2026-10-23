using System.Net;
using System.Net.Mail;
using Enppi.Workload.Api.Data;
using Enppi.Workload.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Enppi.Workload.Api.Services;

public interface INotificationSender
{
    Task<string?> SendAsync(string to, string subject, string body, CancellationToken ct);
}

/// <summary>Sends through the configured SMTP relay (Notifications:Smtp:Host). Without one, messages are only recorded
/// in the Notifications table and shown on Cycle Control.</summary>
public class SmtpNotificationSender(IConfiguration config, ILogger<SmtpNotificationSender> log) : INotificationSender
{
    public async Task<string?> SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        var host = config["Notifications:Smtp:Host"];
        if (string.IsNullOrWhiteSpace(host))
        {
            log.LogInformation("Reminder (no SMTP configured) to {To}: {Subject}", to, subject);
            return "Recorded only - no SMTP relay configured";
        }
        try
        {
            using var client = new SmtpClient(host, config.GetValue("Notifications:Smtp:Port", 25))
            {
                EnableSsl = config.GetValue("Notifications:Smtp:EnableSsl", true),
            };
            var userName = config["Notifications:Smtp:User"];
            if (!string.IsNullOrEmpty(userName)) client.Credentials = new NetworkCredential(userName, config["Notifications:Smtp:Password"]);
            using var msg = new MailMessage(config["Notifications:From"] ?? "workload-forecast@enppi.com", to, subject, body);
            await client.SendMailAsync(msg, ct);
            return null;
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException)
        {
            log.LogWarning(ex, "Reminder to {To} failed", to);
            return ex.Message;
        }
    }
}

/// <summary>Reminders two days before the review due date and daily once overdue (at most one per department per day).</summary>
public class ReminderService(IServiceScopeFactory scopes, IConfiguration config, ILogger<ReminderService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(config.GetValue("Notifications:IntervalMinutes", 60));
        if (!config.GetValue("Notifications:Enabled", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<WorkloadDbContext>();
                var sender = scope.ServiceProvider.GetRequiredService<INotificationSender>();
                foreach (var c in await db.Cycles.Where(c => c.Status == CycleStatus.DisciplineReview).ToListAsync(stoppingToken))
                    await SendDueRemindersAsync(db, sender, c, DateOnly.FromDateTime(DateTime.UtcNow), false, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Reminder run failed");
            }
            await Task.Delay(interval, stoppingToken);
        }
    }

    /// <summary>Remind every department that has not submitted. <paramref name="force"/> ignores the due-date window
    /// (Corporate Planning's "Send reminders now").</summary>
    public static async Task<int> SendDueRemindersAsync(WorkloadDbContext db, INotificationSender sender, ForecastCycle c, DateOnly today,
                                                        bool force, CancellationToken ct)
    {
        var settings = await db.LoadSettingsAsync(ct);
        if (!force && (c.ReviewDue is null || today < c.ReviewDue.Value.AddDays(-settings.ReminderDaysBefore))) return 0;
        // during discipline review every department that has not submitted; during approval only returned departments,
        // the only ones that can still change their review
        if (c.Status is not (CycleStatus.DisciplineReview or CycleStatus.Approval)) return 0;
        bool review = c.Status == CycleStatus.DisciplineReview;
        var open = await db.DepartmentReviews.Where(r => r.CycleId == c.Id && (r.Status == DeptReviewStatus.Returned ||
                (review && (r.Status == DeptReviewStatus.NotStarted || r.Status == DeptReviewStatus.InProgress))))
            .ToListAsync(ct);
        var managers = await db.Users.AsNoTracking().Where(u => u.IsActive && u.Role == Roles.DisciplineManager).ToListAsync(ct);
        var names = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Code, d => d.Name, ct);
        int sent = 0;
        foreach (var r in open)
        {
            if (!force && r.LastReminderAt is { } last && (DateTime.UtcNow - last).TotalHours < 23) continue;
            var to = managers.Where(m => m.Departments().Contains(r.DeptCode)).Select(m => m.Email ?? m.Upn).Distinct().ToList();
            if (to.Count == 0) continue;
            bool overdue = c.ReviewDue is { } due && today > due;
            var subject = $"{(overdue ? "Overdue" : "Reminder")}: workload forecast review {c.Name} - {r.DeptCode} {names.GetValueOrDefault(r.DeptCode)}";
            var body = $"The {c.Name} workload forecast review for department {r.DeptCode} is {(overdue ? "overdue" : "due")}" +
                       (c.ReviewDue is { } d ? $" (due {d:dd-MMM-yyyy})" : "") +
                       ". Open the Workload Forecast portal > Discipline Review, propose changes with a reason, or confirm 'no change', then submit." +
                       (r.Status == DeptReviewStatus.Returned ? $"\n\nCorporate Planning returned your review: {r.Note}" : "");
            foreach (var addr in to)
            {
                var error = await sender.SendAsync(addr, subject, body, ct);
                db.Notifications.Add(new Notification { CycleId = c.Id, DeptCode = r.DeptCode, Recipient = addr, Subject = subject, Body = body,
                                                        Sent = error is null, Error = error });
                sent++;
            }
            r.LastReminderAt = DateTime.UtcNow;
            r.ReminderCount++;
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }
}
