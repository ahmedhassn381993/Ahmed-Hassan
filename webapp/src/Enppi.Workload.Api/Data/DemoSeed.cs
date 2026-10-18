using Enppi.Workload.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Enppi.Workload.Api.Data;

/// <summary>Reference data and a fictional demonstration cycle. Department codes and names are ENPPI's; headcounts,
/// projects, users and man-hours are invented so the repository holds no company data. Load real data with the
/// import screens (or engine/export_webapp.py) in each environment.</summary>
public static class DemoSeed
{
    public static readonly (string Code, string Name, string? Parent)[] DepartmentList =
    {
        ("300", "Quality & Environmental Management System", null), ("600", "Health, Safety & Environment", null),
        ("1100", "Process Technology", null), ("1180", "Pre-Commissioning & Commissioning", null),
        ("2100", "Project Engineering Managers / Leaders", null), ("2110", "Document Controllers", null),
        ("2200", "Instruments", null), ("2220", "Telecommunications", null), ("2300", "Civil / Structural Engineering", null),
        ("2410", "Piping Technical", "2400"), ("2420", "Piping Stress Analysis", "2400"), ("2430", "Piping Design", "2400"),
        ("2440", "Pipeline", "2400"), ("2450", "Underground", "2400"), ("2500", "Electrical Engineering", null),
        ("2600", "Offshore Engineering", null), ("2710", "Mechanical Engineering", "2700"), ("2720", "Pressure Vessels", "2700"),
        ("2800", "Buildings Engineering", null), ("3100", "Project Managers / Engineers", null), ("3300", "Cost Engineering", null),
        ("3400", "Construction Supervision", null), ("3500", "Planning", null), ("4100", "Installations (E&I)", null),
        ("5000", "Material Management", null), ("5100", "Procurement", null),
    };

    public static async Task SeedReferenceAsync(WorkloadDbContext db)
    {
        if (await db.Departments.AnyAsync()) return;
        var rnd = new Random(7);
        foreach (var (code, name, parent) in DepartmentList)
            db.Departments.Add(new Department { Code = code, Name = name, ParentCode = parent, Permanent = rnd.Next(8, 60),
                                                Temporary = rnd.Next(0, 8), Managers = rnd.Next(1, 4) });
        await db.SaveChangesAsync();
    }

    public static async Task SeedDemoAsync(WorkloadDbContext db)
    {
        await SeedReferenceAsync(db);
        if (await db.Users.AnyAsync()) return;
        db.Users.AddRange(
            new UserAccount { Upn = "cp.lead@enppi.demo", DisplayName = "Corporate Planning (demo)", Role = Roles.CorporatePlanning },
            new UserAccount { Upn = "planner.one@enppi.demo", DisplayName = "Planning Engineer 1 (demo)", Role = Roles.Planner },
            new UserAccount { Upn = "planner.two@enppi.demo", DisplayName = "Planning Engineer 2 (demo)", Role = Roles.Planner },
            new UserAccount { Upn = "gm.electrical@enppi.demo", DisplayName = "GM Electrical (demo)", Role = Roles.DisciplineManager, DeptCodes = "2500" },
            new UserAccount { Upn = "gm.process@enppi.demo", DisplayName = "GM Process (demo)", Role = Roles.DisciplineManager, DeptCodes = "1100" },
            new UserAccount { Upn = "gm.piping@enppi.demo", DisplayName = "GM Piping (demo)", Role = Roles.DisciplineManager, DeptCodes = "2410,2420,2430,2440,2450" },
            new UserAccount { Upn = "gm.instruments@enppi.demo", DisplayName = "GM Instruments (demo)", Role = Roles.DisciplineManager, DeptCodes = "2200" },
            new UserAccount { Upn = "viewer@enppi.demo", DisplayName = "Management Viewer (demo)", Role = Roles.Viewer },
            new UserAccount { Upn = "admin@enppi.demo", DisplayName = "System Administrator (demo)", Role = Roles.Admin });

        var projects = new (string Code, string Name, string Client, Category Cat, string Planner)[]
        {
            ("9100100", "Demo Gas Plant Expansion - EPC", "Demo Gas Co", Category.Running, "planner.one@enppi.demo"),
            ("9100200", "Demo Refinery Revamp - FEED", "Demo Refining", Category.Running, "planner.one@enppi.demo"),
            ("9100200-1", "Demo Refinery Revamp - Site Support", "Demo Refining", Category.LoanTo, "planner.one@enppi.demo"),
            ("9100300", "Demo Offshore Platform Tie-in", "Demo Petroleum", Category.Running, "planner.two@enppi.demo"),
            ("9100400", "Demo Pipeline Phase II", "Demo Pipelines", Category.UnderSignature, "planner.two@enppi.demo"),
            ("9100500", "Demo Fertilizer Complex - Basic Design", "Demo Fertilizers", Category.Proposal, "planner.two@enppi.demo"),
            ("9100600", "Demo Methanol Plant Study", "Demo Chemicals", Category.Potential, "planner.one@enppi.demo"),
            ("1100100", "Internal / Unforeseen workload", "ENPPI", Category.Internal, "cp.lead@enppi.demo"),
            ("9100700", "Demo Power Station Upgrade", "Demo Power", Category.Running, "planner.two@enppi.demo"),
            ("9100800", "Demo Tank Farm Extension", "Demo Terminals", Category.Proposal, "planner.one@enppi.demo"),
        };
        foreach (var p in projects)
            db.Projects.Add(new Project { Code = p.Code, BaseCode = Rules.BaseCode(p.Code), Name = p.Name, Client = p.Client,
                                          MasterCategory = p.Cat == Category.Internal ? "I" : "P" });
        db.Projects.Add(new Project { Code = "9100900", BaseCode = "9100900", Name = "Demo Desalination Unit", Client = "Demo Water", MasterCategory = "P" });

        var first = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(1);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var cycle = new ForecastCycle
        {
            Name = "Demo cycle", FirstMonth = first, MonthCount = 24, Status = CycleStatus.DisciplineReview,
            PlannerDue = today.AddDays(-7), ReviewDue = today.AddDays(3), ApprovalDue = today.AddDays(10), CreatedBy = "cp.lead@enppi.demo",
        };
        db.Cycles.Add(cycle);
        await db.SaveChangesAsync();

        var rnd = new Random(42);
        var depts = new[] { "1100", "2200", "2300", "2430", "2500", "2710", "3400", "5100" };
        var lines = new List<ForecastLine>();
        foreach (var p in projects)
        {
            int start = rnd.Next(0, 8), length = rnd.Next(6, 18);
            foreach (var d in depts.Where(_ => rnd.NextDouble() < 0.7))
            {
                double peak = rnd.Next(4, 40) * 25;
                var line = new ForecastLine
                {
                    CycleId = cycle.Id, DeptCode = d, SectionCode = d, Category = p.Cat, ProjectCode = p.Code,
                    Activity = $"{p.Code} {p.Name}", PlannerUpn = p.Planner, Origin = LineOrigin.Import,
                };
                for (int m = start; m < Math.Min(24, start + length); m++)
                {
                    double shape = Math.Sin(Math.PI * (m - start + 0.5) / length);
                    line.Values.Add(new ForecastValue { MonthIndex = m, PlannerMH = Math.Round(peak * shape / 25) * 25 });
                }
                lines.Add(line);
            }
        }
        db.Lines.AddRange(lines);
        foreach (var d in DepartmentList)
            db.DepartmentReviews.Add(new DepartmentReview { CycleId = cycle.Id, DeptCode = d.Code });
        await db.SaveChangesAsync();

        // two departments have already answered so the approval queue has work in it
        var elec = lines.Where(l => l.DeptCode == "2500" && !Categories.IsCorporateControlled(l.Category)).Take(3).ToList();
        foreach (var (l, i) in elec.Select((l, i) => (l, i)))
        {
            var p = new Proposal { CycleId = cycle.Id, LineId = l.Id, DeptCode = "2500", Kind = ProposalKind.Change,
                                   Status = ProposalStatus.Submitted, SubmittedAt = DateTime.UtcNow, CreatedBy = "gm.electrical@enppi.demo",
                                   Reason = i switch
                                   {
                                       0 => "Electrical scope grew after the client added two substations; extra design hours through commissioning.",
                                       1 => "Load study finished early, so the last two months are no longer needed.",
                                       _ => "Vendor data arrives three months late; design effort moves later and runs past the planned finish.",
                                   } };
            var vals = l.Values.OrderBy(v => v.MonthIndex).ToList();
            if (i == 0) foreach (var v in vals) p.Values.Add(new ProposalValue { MonthIndex = v.MonthIndex, ProposedMH = (v.PlannerMH ?? 0) * 1.3 });
            if (i == 1) foreach (var v in vals.TakeLast(2)) p.Values.Add(new ProposalValue { MonthIndex = v.MonthIndex, ProposedMH = 0 });
            if (i == 2)
            {
                var last = vals.Last().MonthIndex;
                for (int k = 1; k <= 3 && last + k < 24; k++) p.Values.Add(new ProposalValue { MonthIndex = last + k, ProposedMH = 100 });
            }
            p.ChangeTypes = Rules.Classify(Enumerable.Range(0, 24).Select(m => l.Values.FirstOrDefault(v => v.MonthIndex == m)?.PlannerMH ?? 0).ToArray(),
                                           Enumerable.Range(0, 24).Select(m => p.Values.FirstOrDefault(v => v.MonthIndex == m)?.ProposedMH).ToArray()).TypesText;
            db.Proposals.Add(p);
        }
        var newWork = new Proposal
        {
            CycleId = cycle.Id, DeptCode = "2500", Kind = ProposalKind.NewAllocation, Status = ProposalStatus.Submitted,
            SubmittedAt = DateTime.UtcNow, CreatedBy = "gm.electrical@enppi.demo", NewProjectCode = "9100900",
            NewActivity = "9100900 Demo Desalination Unit", NewCategory = Category.Proposal, NewSectionCode = "2500", ChangeTypes = "New Project",
            Reason = "Proposal team asked Electrical for the load list and single-line diagram; not in the planner forecast.",
        };
        for (int m = 2; m < 8; m++) newWork.Values.Add(new ProposalValue { MonthIndex = m, ProposedMH = 150 });
        db.Proposals.Add(newWork);
        var rev = await db.DepartmentReviews.FirstAsync(r => r.CycleId == cycle.Id && r.DeptCode == "2500");
        rev.Status = DeptReviewStatus.Submitted;
        rev.SubmittedAt = DateTime.UtcNow;
        rev.SubmittedBy = "gm.electrical@enppi.demo";
        var proc = await db.DepartmentReviews.FirstAsync(r => r.CycleId == cycle.Id && r.DeptCode == "300");
        proc.Status = DeptReviewStatus.NoChangeConfirmed;
        proc.SubmittedAt = DateTime.UtcNow;
        db.Log("system", "Seed", "Cycle", cycle.Id.ToString(), cycle.Id, "Demonstration data");
        await db.SaveChangesAsync();
    }
}
