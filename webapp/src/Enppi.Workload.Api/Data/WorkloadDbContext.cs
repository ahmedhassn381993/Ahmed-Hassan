using Enppi.Workload.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Enppi.Workload.Api.Data;

public class WorkloadDbContext(DbContextOptions<WorkloadDbContext> options) : DbContext(options)
{
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ForecastCycle> Cycles => Set<ForecastCycle>();
    public DbSet<ForecastLine> Lines => Set<ForecastLine>();
    public DbSet<ForecastValue> Values => Set<ForecastValue>();
    public DbSet<Proposal> Proposals => Set<Proposal>();
    public DbSet<ProposalValue> ProposalValues => Set<ProposalValue>();
    public DbSet<DepartmentReview> DepartmentReviews => Set<DepartmentReview>();
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ForecastCycle>().Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
        b.Entity<ForecastCycle>().HasIndex(c => c.Name).IsUnique();

        b.Entity<ForecastLine>(e =>
        {
            e.Property(l => l.Category).HasConversion<string>().HasMaxLength(20);
            e.Property(l => l.ApprovedCategory).HasConversion<string>().HasMaxLength(20);
            e.Property(l => l.AfterCategory).HasConversion<string>().HasMaxLength(20);
            e.Property(l => l.Origin).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(l => new { l.CycleId, l.DeptCode });
            e.HasIndex(l => new { l.CycleId, l.ProjectCode });
            e.HasOne<ForecastCycle>().WithMany().HasForeignKey(l => l.CycleId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(l => l.Values).WithOne().HasForeignKey(v => v.LineId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ForecastValue>().HasKey(v => new { v.LineId, v.MonthIndex });

        b.Entity<Proposal>(e =>
        {
            e.Property(p => p.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.RequestedCategory).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.NewCategory).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(p => new { p.CycleId, p.DeptCode });
            e.HasIndex(p => new { p.CycleId, p.Status });
            e.HasOne(p => p.Line).WithMany().HasForeignKey(p => p.LineId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<ForecastCycle>().WithMany().HasForeignKey(p => p.CycleId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(p => p.Values).WithOne().HasForeignKey(v => v.ProposalId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ProposalValue>().HasKey(v => new { v.ProposalId, v.MonthIndex });

        b.Entity<DepartmentReview>(e =>
        {
            e.HasKey(r => new { r.CycleId, r.DeptCode });
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<AuditEntry>().HasIndex(a => new { a.CycleId, a.At });
        b.Entity<Notification>().HasIndex(n => new { n.CycleId, n.DeptCode });
    }

    public async Task<WorkloadSettings> LoadSettingsAsync(CancellationToken ct = default)
    {
        var map = await Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        double D(string k, double d) => map.TryGetValue(k, out var v) && double.TryParse(v,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : d;
        var s = new WorkloadSettings();
        return s with
        {
            PermanentEffectiveness = D(nameof(s.PermanentEffectiveness), s.PermanentEffectiveness),
            HoursPerManMonth = D(nameof(s.HoursPerManMonth), s.HoursPerManMonth),
            BulkMaxPct = D(nameof(s.BulkMaxPct), s.BulkMaxPct),
            BulkMaxMH = D(nameof(s.BulkMaxMH), s.BulkMaxMH),
            JustifyPct = D(nameof(s.JustifyPct), s.JustifyPct),
            JustifyMH = D(nameof(s.JustifyMH), s.JustifyMH),
            JustifyMinChars = (int)D(nameof(s.JustifyMinChars), s.JustifyMinChars),
            ReminderDaysBefore = (int)D(nameof(s.ReminderDaysBefore), s.ReminderDaysBefore),
        };
    }

    public void Log(string user, string action, string entity, string key, int? cycleId, string? details = null) =>
        Audit.Add(new AuditEntry { User = user, Action = action, Entity = entity, EntityKey = key, CycleId = cycleId,
                                   Details = details is { Length: > 4000 } ? details[..4000] : details });
}
