using Enppi.Workload.Api.Data;
using Enppi.Workload.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Enppi.Workload.Api.Services;

/// <summary>One forecast row in the three scenarios (plus the final value), month by month.</summary>
public record ScenarioRow(
    int? LineId, int? ProposalId, string DeptCode, string SectionCode, Category Category, string ProjectCode, string Activity,
    string? PlannerUpn, double[] Planning, double[] Discipline, double[] Final, double[] After, Proposal? Proposal,
    ChangeSummary Change)
{
    public bool Concern => Proposal is not null && (Change.MonthsChanged.Count > 0 || Proposal.Kind != ProposalKind.Change);
}

public record DepartmentSummary(
    string DeptCode, string Department, string ReviewStatus, double Planning, double Discipline, double Final, double After,
    double GrossIncrease, double GrossReduction, int ConcernItems, double CapacityPerMonth, double EffectiveHeads,
    double UtilisationPlanning, double UtilisationDiscipline, double UtilisationFinal, double FirmUtilisationDiscipline,
    int OverloadMonths, double ImplementationRate)
{
    public double Net => GrossIncrease + GrossReduction;
    public double DeviationPct => Planning > 0 ? Net / Planning : 0;
}

public record ProjectSeverity(
    string ProjectCode, string Name, string? Client, int Points, string Level, int DeptsRaising, string Departments,
    double Planning, double Discipline, double GrossIncrease, double GrossReduction, int MonthsAffected, bool HasNew,
    bool HasExtension, string Types, bool InMaster)
{
    public double Net => GrossIncrease + GrossReduction;
}

public record DeviationItem(
    int? ProposalId, int? LineId, string Dept, string Section, string Category, string Project, string Activity, string Kind,
    string Status, string Reason, double Planning, double Discipline, double Final, double After, double GrossIncrease,
    double GrossReduction, double Net, string Types, string BriefCategories, string MonthsChanged, string? RequestedCategory,
    string Implementation, string? ReviewedBy, string? ReviewComment);

public class CycleSnapshot
{
    public required ForecastCycle Cycle { get; init; }
    public required List<ScenarioRow> Rows { get; init; }
    public required Dictionary<string, Department> Departments { get; init; }
    public required Dictionary<string, Project> Projects { get; init; }
    public required Dictionary<string, DepartmentReview> Reviews { get; init; }
    public required WorkloadSettings Settings { get; init; }
    public DateOnly[] Months => Enumerable.Range(0, Cycle.MonthCount).Select(Cycle.Month).ToArray();
}

public class WorkloadService(WorkloadDbContext db)
{
    public async Task<CycleSnapshot> SnapshotAsync(int cycleId, CancellationToken ct = default)
    {
        var cycle = await db.Cycles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cycleId, ct)
                    ?? throw new KeyNotFoundException($"Cycle {cycleId} not found");
        var lines = await db.Lines.AsNoTracking().Include(l => l.Values).Where(l => l.CycleId == cycleId).ToListAsync(ct);
        var proposals = await db.Proposals.AsNoTracking().Include(p => p.Values)
            .Where(p => p.CycleId == cycleId && p.Status != ProposalStatus.Draft).ToListAsync(ct);
        var settings = await db.LoadSettingsAsync(ct);
        var depts = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Code, ct);
        var codes = lines.Select(l => l.ProjectCode).Concat(proposals.Where(p => p.NewProjectCode != null).Select(p => p.NewProjectCode!))
            .Distinct().ToList();
        var projects = await db.Projects.AsNoTracking().Where(p => codes.Contains(p.Code)).ToDictionaryAsync(p => p.Code, ct);
        var reviews = await db.DepartmentReviews.AsNoTracking().Where(r => r.CycleId == cycleId).ToDictionaryAsync(r => r.DeptCode, ct);
        return new CycleSnapshot
        {
            Cycle = cycle, Rows = BuildRows(cycle, lines, proposals), Departments = depts, Projects = projects, Reviews = reviews,
            Settings = settings,
        };
    }

    /// <summary>Planning = planner value; Discipline = proposed where populated, planner value where blank; Final = approved
    /// where approved, otherwise planner; After = re-extracted value (or Final until an after extract is loaded).</summary>
    public static List<ScenarioRow> BuildRows(ForecastCycle cycle, List<ForecastLine> lines, List<Proposal> proposals)
    {
        int n = cycle.MonthCount;
        var byLine = proposals.Where(p => p.LineId != null && p.Kind != ProposalKind.NewAllocation)
            .GroupBy(p => p.LineId!.Value).ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Kind == ProposalKind.Change).First());
        var rows = new List<ScenarioRow>();
        var deptBases = lines.Select(l => (l.DeptCode, Rules.BaseCode(l.ProjectCode))).ToHashSet();
        foreach (var l in lines)
        {
            var v = l.Values.ToDictionary(x => x.MonthIndex);
            double[] planning = new double[n], final = new double[n], after = new double[n], disc = new double[n];
            var proposedArr = new double?[n];
            byLine.TryGetValue(l.Id, out var p);
            var pv = p?.Values.ToDictionary(x => x.MonthIndex) ?? new();
            for (int m = 0; m < n; m++)
            {
                v.TryGetValue(m, out var fv);
                planning[m] = fv?.PlannerMH ?? 0;
                final[m] = fv is null ? 0 : Rules.FinalValue(fv);
                after[m] = cycle.HasAfterData ? fv?.AfterMH ?? 0 : final[m];
                proposedArr[m] = pv.TryGetValue(m, out var x) ? x.ProposedMH : null;
                disc[m] = Rules.DisciplineValue(planning[m], proposedArr[m]);
            }
            // a line created from an approved new-allocation request has no planning value: the request is its discipline value
            var isNewLine = l.Origin == LineOrigin.DepartmentRequest;
            var request = isNewLine ? proposals.FirstOrDefault(q => q.LineId == l.Id && q.Kind == ProposalKind.NewAllocation) : null;
            if (request is not null)
            {
                var rv = request.Values.ToDictionary(x => x.MonthIndex);
                for (int m = 0; m < n; m++) disc[m] = rv.TryGetValue(m, out var x) ? x.ProposedMH ?? 0 : 0;
                p = request;
                proposedArr = Enumerable.Range(0, n).Select(m => (double?)disc[m]).ToArray();
            }
            var change = p is null
                ? Rules.Classify(planning, proposedArr)
                : Rules.Classify(planning, proposedArr, p.Kind,
                                 p.Kind == ProposalKind.NewAllocation && deptBases.Contains((l.DeptCode, Rules.BaseCode(l.ProjectCode))) &&
                                 lines.Count(x => x.DeptCode == l.DeptCode && Rules.BaseCode(x.ProjectCode) == Rules.BaseCode(l.ProjectCode)) > 1);
            rows.Add(new ScenarioRow(l.Id, p?.Id, l.DeptCode, l.SectionCode, l.Category, l.ProjectCode, l.Activity, l.PlannerUpn,
                                     planning, disc, final, after, p, change));
        }
        // new allocations still waiting for a decision exist only as requests
        foreach (var p in proposals.Where(p => p.Kind == ProposalKind.NewAllocation && p.LineId == null))
        {
            var rv = p.Values.ToDictionary(x => x.MonthIndex);
            var disc = Enumerable.Range(0, n).Select(m => rv.TryGetValue(m, out var x) ? x.ProposedMH ?? 0 : 0).ToArray();
            var newPhase = deptBases.Contains((p.DeptCode, Rules.BaseCode(p.NewProjectCode ?? "")));
            var change = Rules.Classify(new double[n], disc.Select(x => (double?)x).ToArray(), ProposalKind.NewAllocation, newPhase);
            rows.Add(new ScenarioRow(null, p.Id, p.DeptCode, p.NewSectionCode ?? p.DeptCode, p.NewCategory ?? Category.Proposal,
                                     p.NewProjectCode ?? "NEW", p.NewActivity ?? "", null, new double[n], disc, new double[n],
                                     new double[n], p, change));
        }
        return rows;
    }

    public static List<DepartmentSummary> Departments(CycleSnapshot s)
    {
        int n = s.Cycle.MonthCount;
        var result = new List<DepartmentSummary>();
        foreach (var d in s.Departments.Values.Where(d => d.IsActive).OrderBy(d => int.TryParse(d.Code, out var x) ? x : 0))
        {
            var rows = s.Rows.Where(r => r.DeptCode == d.Code).ToList();
            double cap = Rules.CapacityPerMonth(d, s.Settings);
            double Sum(Func<ScenarioRow, double[]> f) => rows.Sum(r => f(r).Sum());
            double[] Monthly(Func<ScenarioRow, double[]> f, bool firmOnly = false) =>
                Enumerable.Range(0, n).Select(m => rows.Where(r => !firmOnly || Categories.IsFirm(r.Category)).Sum(r => f(r)[m])).ToArray();
            var disc = Monthly(r => r.Discipline);
            var firm = Monthly(r => r.Discipline, true);
            var concern = rows.Where(r => r.Concern).ToList();
            double requested = concern.Sum(r => r.Change.GrossChange);
            double implemented = concern.Sum(r => ImplementedShare(r) * r.Change.GrossChange);
            s.Reviews.TryGetValue(d.Code, out var rev);
            double U(double total) => cap > 0 ? total / n / cap : 0;
            result.Add(new DepartmentSummary(
                d.Code, d.Name, (rev?.Status ?? DeptReviewStatus.NotStarted).ToString(), Sum(r => r.Planning), Sum(r => r.Discipline),
                Sum(r => r.Final), Sum(r => r.After), concern.Sum(r => r.Change.GrossIncrease), concern.Sum(r => r.Change.GrossReduction),
                concern.Count, cap, Rules.EffectiveHeads(d, s.Settings), U(Sum(r => r.Planning)), U(disc.Sum()), U(Sum(r => r.Final)),
                U(firm.Sum()), cap > 0 ? firm.Count(x => x > cap) : 0, requested > 0 ? implemented / requested : 0));
        }
        return result;
    }

    /// <summary>Share of the requested change that the stored values now carry: the approved (final) value where no after
    /// extract is loaded, the after-implementation value once one is.</summary>
    public static double ImplementedShare(ScenarioRow r)
    {
        double req = 0, got = 0;
        foreach (var m in r.Change.MonthsChanged)
        {
            double want = r.Discipline[m] - r.Planning[m], have = r.After[m] - r.Planning[m];
            req += Math.Abs(want);
            if (Math.Sign(want) == Math.Sign(have)) got += Math.Min(Math.Abs(want), Math.Abs(have));
        }
        return req > 0 ? got / req : 0;
    }

    public static string ImplementationStatus(ScenarioRow r, bool hasAfter)
    {
        if (r.Proposal is null) return "No request";
        if (r.Proposal.Status is ProposalStatus.Submitted) return "Awaiting decision";
        if (r.Proposal.Status is ProposalStatus.Returned) return "Returned to department";
        if (r.Proposal.Status is ProposalStatus.Rejected) return "Rejected";
        if (r.Change.MonthsChanged.Count == 0) return "Code or category change - confirm in Primavera";
        if (!hasAfter) return r.Proposal.ImplementedAt is null ? "Approved - to implement" : "Marked implemented - awaiting extract";
        // after data loaded: compare with the approved (final) value
        double gap = r.Change.MonthsChanged.Sum(m => Math.Abs(r.After[m] - r.Final[m]));
        if (gap < Rules.Tol) return "Implemented";
        double moved = r.Change.MonthsChanged.Sum(m => Math.Abs(r.After[m] - r.Planning[m]));
        return moved < Rules.Tol ? "Not implemented" : "Partially implemented";
    }

    public static List<ProjectSeverity> Projects(CycleSnapshot s)
    {
        var result = new List<ProjectSeverity>();
        foreach (var g in s.Rows.GroupBy(r => r.ProjectCode))
        {
            var concern = g.Where(r => r.Concern).ToList();
            double planning = g.Sum(r => r.Planning.Sum()), disc = g.Sum(r => r.Discipline.Sum());
            double inc = concern.Sum(r => r.Change.GrossIncrease), red = concern.Sum(r => r.Change.GrossReduction);
            var depts = concern.Select(r => r.DeptCode).Distinct().OrderBy(x => x).ToList();
            int months = concern.SelectMany(r => r.Change.MonthsChanged).Distinct().Count();
            bool hasNew = concern.Any(r => r.Proposal?.Kind == ProposalKind.NewAllocation);
            bool hasExt = concern.Any(r => r.Change.ExtensionMH >= Rules.Tol);
            var (pts, level) = concern.Count == 0 ? (0, "No Concern")
                : Rules.Severity(inc - red, inc + red, planning, depts.Count, months, hasNew, hasExt);
            s.Projects.TryGetValue(g.Key, out var proj);
            var name = proj?.Name ?? g.Select(r => r.Activity).OrderByDescending(a => a.Length).FirstOrDefault() ?? "";
            var types = string.Join("; ", concern.SelectMany(r => r.Change.Types).Distinct());
            result.Add(new ProjectSeverity(g.Key, name, proj?.Client, pts, level, depts.Count, string.Join(", ", depts), planning, disc,
                                           inc, red, months, hasNew, hasExt, types, proj?.InMaster ?? false));
        }
        return result.OrderByDescending(p => p.Points).ThenByDescending(p => p.GrossIncrease - p.GrossReduction).ToList();
    }

    public static object Dashboard(CycleSnapshot s)
    {
        var depts = Departments(s);
        var months = s.Months;
        double totalCap = depts.Sum(d => d.CapacityPerMonth);
        var monthly = months.Select((m, i) => new
        {
            month = m,
            planning = s.Rows.Sum(r => r.Planning[i]),
            discipline = s.Rows.Sum(r => r.Discipline[i]),
            final = s.Rows.Sum(r => r.Final[i]),
            after = s.Rows.Sum(r => r.After[i]),
            capacity = totalCap,
        }).ToList();
        var concern = s.Rows.Where(r => r.Concern).ToList();
        double requested = concern.Sum(r => r.Change.GrossChange);
        var projects = Projects(s);
        var bridge = new
        {
            newWork = concern.Where(r => r.Proposal!.Kind == ProposalKind.NewAllocation).Sum(r => r.Change.Net),
            removed = concern.Where(r => r.Change.Types.Contains("Removed Allocation")).Sum(r => r.Change.Net),
            extension = concern.Where(r => r.Proposal!.Kind != ProposalKind.NewAllocation && !r.Change.Types.Contains("Removed Allocation"))
                .Sum(r => r.Change.ExtensionMH + r.Change.EarlyStartMH),
        };
        var other = concern.Where(r => r.Proposal!.Kind != ProposalKind.NewAllocation && !r.Change.Types.Contains("Removed Allocation")).ToList();
        double increase = other.Sum(r => r.Change.GrossIncrease - r.Change.ExtensionMH - r.Change.EarlyStartMH);
        double reduction = other.Sum(r => r.Change.GrossReduction);
        return new
        {
            cycle = s.Cycle,
            kpis = new
            {
                planning = depts.Sum(d => d.Planning), discipline = depts.Sum(d => d.Discipline), final = depts.Sum(d => d.Final),
                after = depts.Sum(d => d.After), grossIncrease = depts.Sum(d => d.GrossIncrease), grossReduction = depts.Sum(d => d.GrossReduction),
                concernItems = concern.Count, requested,
                implementationRate = requested > 0 ? concern.Sum(r => ImplementedShare(r) * r.Change.GrossChange) / requested : 0,
                newAllocations = concern.Count(r => r.Proposal!.Kind == ProposalKind.NewAllocation),
                pendingDecisions = concern.Count(r => r.Proposal!.Status == ProposalStatus.Submitted),
                critical = projects.Count(p => p.Level == "Critical"), high = projects.Count(p => p.Level == "High"),
                departmentsSubmitted = s.Reviews.Values.Count(r => r.Status is DeptReviewStatus.Submitted or DeptReviewStatus.NoChangeConfirmed),
                departments = depts.Count, capacityPerMonth = totalCap,
            },
            bridge = new { bridge.newWork, increase, bridge.extension, reduction, bridge.removed },
            monthly,
            departments = depts,
            topProjects = projects.Where(p => p.Level != "No Concern").Take(10),
        };
    }

    public static IEnumerable<DeviationItem> DeviationRegister(CycleSnapshot s) =>
        s.Rows.Where(r => r.Proposal is not null)
            .OrderBy(r => r.DeptCode).ThenByDescending(r => Math.Abs(r.Change.Net))
            .Select(r => new DeviationItem(
                r.ProposalId, r.LineId, r.DeptCode, r.SectionCode, Categories.Label(r.Category), r.ProjectCode, r.Activity,
                r.Proposal!.Kind.ToString(), r.Proposal.Status.ToString(), r.Proposal.Reason, r.Planning.Sum(), r.Discipline.Sum(),
                r.Final.Sum(), r.After.Sum(), r.Change.GrossIncrease, r.Change.GrossReduction, r.Change.Net, r.Change.TypesText,
                r.Change.BriefCategories, string.Join(", ", r.Change.MonthsChanged.Select(m => s.Cycle.Month(m).ToString("MMM-yy"))),
                r.Proposal.RequestedCategory is { } c ? Categories.Label(c) : null, ImplementationStatus(r, s.Cycle.HasAfterData),
                r.Proposal.ReviewedBy, r.Proposal.ReviewComment));
}
