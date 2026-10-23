using System.Text.Json;
using Enppi.Workload.Api.Data;
using Enppi.Workload.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Enppi.Workload.Api.Services;

/// <summary>One forecast row in the three scenarios (plus the final value), month by month. <see cref="Proposal"/> is the
/// man-hour request on the row (a change or a new allocation), <see cref="CategoryRequest"/> a request to move it to another
/// category. <see cref="Category"/> is the Planning Input category, which the discipline scenario keeps too (as the engine
/// does: a requested move is paired with its baseline row); the final and after scenarios carry their own.</summary>
public record ScenarioRow(
    int? LineId, int? ProposalId, string DeptCode, string SectionCode, Category Category, string ProjectCode, string Activity,
    string? PlannerUpn, double[] Planning, double[] Discipline, double[] Final, double[] After, Proposal? Proposal,
    ChangeSummary Change, Proposal? CategoryRequest = null, Category? DisciplineCategory = null, Category? FinalCategory = null,
    Category? AfterCategory = null)
{
    public bool Concern => (Proposal is not null && (Change.MonthsChanged.Count > 0 || Proposal.Kind != ProposalKind.Change)) ||
                           CategoryRequest is not null;

    /// <summary>Change types of the row. A requested move to another category adds "Phase Transfer" when man-hours change
    /// too, and is "Phase Label Change Only" when they do not (the engine's names).</summary>
    public IReadOnlyList<string> Types => CategoryRequest is null || Change.Types.Contains("Phase Transfer") ? Change.Types
        : Change.MonthsChanged.Count == 0 && Proposal?.Kind != ProposalKind.NewAllocation ? new[] { "Phase Label Change Only" }
        : Change.Types.Append("Phase Transfer").ToList();
    public string TypesText => string.Join("; ", Types);
    public string BriefCategories => string.Join("; ", Types.Select(Rules.BriefCategory).Distinct());

    /// <summary>Every request on the row (each is decided on its own).</summary>
    public IEnumerable<Proposal> Requests => new[] { Proposal, CategoryRequest }.OfType<Proposal>();

    public Category CategoryIn(string scenario) => scenario switch
    {
        ExportService.DisciplineComment => DisciplineCategory ?? Category,
        ExportService.AfterImplementation => AfterCategory ?? Category,
        "Final" => FinalCategory ?? Category,
        _ => Category,
    };
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

    /// <summary>The project master record of a code: the code itself, or its base project for a phase (5248200-1).</summary>
    public Project? MasterOf(string code) =>
        Projects.GetValueOrDefault(code) is { InMaster: true } p ? p
        : Projects.GetValueOrDefault(Rules.BaseCode(code)) is { InMaster: true } b ? b
        : null;

    /// <summary>The engine's master check: no code, the internal workload code, not in the master, or an internal /
    /// overhead account used for project work; empty when the code is a normal master project.</summary>
    public string MasterFlag(string code)
    {
        if (code.StartsWith("UNCODED", StringComparison.OrdinalIgnoreCase)) return "No project code";
        if (Rules.BaseCode(code) == "1100100") return "Internal / unforeseen workload code";
        var m = MasterOf(code);
        if (m is null) return "Not in project master (new or proposal code)";
        return m.MasterCategory is "I" or "O" || m.Name.Contains("DEVELOPMENT AND CUSTOMER", StringComparison.OrdinalIgnoreCase)
            ? "Internal / overhead account used for project work" : "";
    }
}

public class WorkloadService(WorkloadDbContext db)
{
    /// <summary>The cycle as stored. <paramref name="scope"/> limits it to some departments (a discipline manager's).</summary>
    public async Task<CycleSnapshot> SnapshotAsync(int cycleId, IReadOnlyCollection<string>? scope = null, CancellationToken ct = default)
    {
        var cycle = await db.Cycles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cycleId, ct)
                    ?? throw new KeyNotFoundException($"Cycle {cycleId} not found");
        var lineQuery = db.Lines.AsNoTracking().Include(l => l.Values).Where(l => l.CycleId == cycleId);
        var proposalQuery = db.Proposals.AsNoTracking().Include(p => p.Values).Where(p => p.CycleId == cycleId && p.Status != ProposalStatus.Draft);
        var deptQuery = db.Departments.AsNoTracking();
        var reviewQuery = db.DepartmentReviews.AsNoTracking().Where(r => r.CycleId == cycleId);
        if (scope is not null)
        {
            var ds = scope.ToList();
            lineQuery = lineQuery.Where(l => ds.Contains(l.DeptCode));
            proposalQuery = proposalQuery.Where(p => ds.Contains(p.DeptCode));
            deptQuery = deptQuery.Where(d => ds.Contains(d.Code));
            reviewQuery = reviewQuery.Where(r => ds.Contains(r.DeptCode));
        }
        var lines = await lineQuery.ToListAsync(ct);
        var proposals = await proposalQuery.ToListAsync(ct);
        var settings = await db.LoadSettingsAsync(ct);
        var depts = await deptQuery.ToDictionaryAsync(d => d.Code, ct);
        if (cycle.LockedCapacity is { Length: > 0 } frozen && JsonSerializer.Deserialize<LockedCapacity>(frozen) is { } locked)
        {
            // a locked cycle keeps the headcount and factors it was locked with
            settings = locked.Settings;
            foreach (var d in depts.Values)
                if (locked.Heads.TryGetValue(d.Code, out var h) && h.Length >= 2) (d.Permanent, d.Temporary) = (h[0], h[1]);
                else (d.Permanent, d.Temporary) = (0, 0);
        }
        var codes = lines.Select(l => l.ProjectCode).Concat(proposals.Where(p => p.NewProjectCode != null).Select(p => p.NewProjectCode!))
            .SelectMany(c => new[] { c, Rules.BaseCode(c) }).Distinct().ToList();
        var projects = await db.Projects.AsNoTracking().Where(p => codes.Contains(p.Code)).ToDictionaryAsync(p => p.Code, ct);
        var reviews = await reviewQuery.ToDictionaryAsync(r => r.DeptCode, ct);
        return new CycleSnapshot
        {
            Cycle = cycle, Rows = BuildRows(cycle, lines, proposals), Departments = depts, Projects = projects, Reviews = reviews,
            Settings = settings,
        };
    }

    /// <summary>Lines of the Planning Input (planner or planning import), as opposed to lines created from a department's
    /// request or found only in the after extract.</summary>
    public static bool InBaseline(ForecastLine l) => l.Origin is LineOrigin.Planner or LineOrigin.Import;

    /// <summary>Planning = planner value; Discipline = proposed where populated, planner value where blank; Final = approved
    /// where approved, otherwise planner; After = re-extracted value (or Final until an after extract is loaded).</summary>
    public static List<ScenarioRow> BuildRows(ForecastCycle cycle, List<ForecastLine> lines, List<Proposal> proposals)
    {
        int n = cycle.MonthCount;
        Dictionary<int, Proposal> OnLine(ProposalKind kind) => proposals.Where(p => p.LineId != null && p.Kind == kind)
            .GroupBy(p => p.LineId!.Value).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Id).First());
        var changes = OnLine(ProposalKind.Change);
        var moves = OnLine(ProposalKind.CategoryChange);
        var requests = OnLine(ProposalKind.NewAllocation);
        var baseline = lines.Where(InBaseline).ToList();
        var deptBases = baseline.Select(l => (l.DeptCode, Rules.BaseCode(l.ProjectCode))).ToHashSet();
        var baseKeys = baseline.Select(l => (l.DeptCode, l.SectionCode, l.Category, l.ProjectCode)).ToHashSet();
        // a second request with the same department / sub-department / category / code reuses the code, as in the engine
        var reused = proposals.Where(p => p.Kind == ProposalKind.NewAllocation)
            .GroupBy(p => (p.DeptCode, p.NewSectionCode ?? p.DeptCode, p.NewCategory ?? Category.Proposal, p.NewProjectCode ?? ""))
            .SelectMany(g => g.OrderBy(p => p.Id).Skip(1)).Select(p => p.Id).ToHashSet();
        string NewType(Proposal p, string dept, string section, Category cat, string code) =>
            Rules.NewAllocationType(deptBases.Contains((dept, Rules.BaseCode(code))),
                                    baseKeys.Contains((dept, section, cat, code)) || reused.Contains(p.Id));
        var rows = new List<ScenarioRow>();
        foreach (var l in lines)
        {
            var v = l.Values.ToDictionary(x => x.MonthIndex);
            double[] planning = new double[n], final = new double[n], after = new double[n], disc = new double[n];
            var proposedArr = new double?[n];
            changes.TryGetValue(l.Id, out var p);
            moves.TryGetValue(l.Id, out var move);
            // a line that carries a new-allocation request has no planning value: the request is its discipline value
            if (requests.TryGetValue(l.Id, out var request)) p = request;
            var pv = p?.Values.ToDictionary(x => x.MonthIndex) ?? new();
            for (int m = 0; m < n; m++)
            {
                v.TryGetValue(m, out var fv);
                planning[m] = fv?.PlannerMH ?? 0;
                final[m] = fv is null ? 0 : Rules.FinalValue(fv);
                after[m] = cycle.HasAfterData ? fv?.AfterMH ?? 0 : final[m];
                proposedArr[m] = pv.TryGetValue(m, out var x) ? x.ProposedMH : null;
                disc[m] = request is not null ? proposedArr[m] ?? 0 : Rules.DisciplineValue(planning[m], proposedArr[m]);
            }
            if (request is not null) proposedArr = disc.Select(x => (double?)x).ToArray();
            var change = p is null ? Rules.Classify(planning, proposedArr)
                : Rules.Classify(planning, proposedArr, p.Kind,
                                 p.Kind == ProposalKind.NewAllocation ? NewType(p, l.DeptCode, l.SectionCode, l.Category, l.ProjectCode) : null);
            var finalCat = l.ApprovedCategory ?? l.Category;
            rows.Add(new ScenarioRow(l.Id, p?.Id ?? move?.Id, l.DeptCode, l.SectionCode, l.Category, l.ProjectCode, l.Activity, l.PlannerUpn,
                                     planning, disc, final, after, p, change, move, l.Category, finalCat,
                                     cycle.HasAfterData ? l.AfterCategory ?? l.Category : finalCat));
        }
        // new allocations still waiting for a decision exist only as requests
        foreach (var p in proposals.Where(p => p.Kind == ProposalKind.NewAllocation && p.LineId == null))
        {
            var rv = p.Values.ToDictionary(x => x.MonthIndex);
            var disc = Enumerable.Range(0, n).Select(m => rv.TryGetValue(m, out var x) ? x.ProposedMH ?? 0 : 0).ToArray();
            var section = p.NewSectionCode ?? p.DeptCode;
            var category = p.NewCategory ?? Category.Proposal;
            var code = p.NewProjectCode ?? "NEW";
            var change = Rules.Classify(new double[n], disc.Select(x => (double?)x).ToArray(), ProposalKind.NewAllocation,
                                        NewType(p, p.DeptCode, section, category, code));
            rows.Add(new ScenarioRow(null, p.Id, p.DeptCode, section, category, code, p.NewActivity ?? "", null, new double[n], disc,
                                     new double[n], new double[n], p, change));
        }
        return rows;
    }

    public static List<DepartmentSummary> Departments(CycleSnapshot s)
    {
        int n = s.Cycle.MonthCount;
        var result = new List<DepartmentSummary>();
        // active departments, plus any department code that has forecast rows (unknown or inactive codes stay visible)
        var codes = s.Departments.Values.Where(d => d.IsActive).Select(d => d.Code).Union(s.Rows.Select(r => r.DeptCode))
            .Distinct().OrderBy(c => int.TryParse(c, out var x) ? x : int.MaxValue).ThenBy(c => c);
        foreach (var code in codes)
        {
            var d = s.Departments.GetValueOrDefault(code) ?? new Department { Code = code, Name = "(not in the department master)" };
            var rows = s.Rows.Where(r => r.DeptCode == code).ToList();
            double cap = Rules.CapacityPerMonth(d, s.Settings);
            double Sum(Func<ScenarioRow, double[]> f) => rows.Sum(r => f(r).Sum());
            var disc = Enumerable.Range(0, n).Select(m => rows.Sum(r => r.Discipline[m])).ToArray();
            var firm = Enumerable.Range(0, n)
                .Select(m => rows.Where(r => Categories.IsFirm(r.CategoryIn(ExportService.DisciplineComment))).Sum(r => r.Discipline[m])).ToArray();
            var concern = rows.Where(r => r.Concern).ToList();
            double requested = concern.Sum(r => r.Change.GrossChange);
            double implemented = concern.Sum(r => ImplementedShare(r) * r.Change.GrossChange);
            s.Reviews.TryGetValue(code, out var rev);
            double U(double total) => cap > 0 ? total / n / cap : 0;
            result.Add(new DepartmentSummary(
                code, d.Name, (rev?.Status ?? DeptReviewStatus.NotStarted).ToString(), Sum(r => r.Planning), Sum(r => r.Discipline),
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

    /// <summary>Where a request stands, from decision to the after extract. <paramref name="p"/> defaults to the row's
    /// man-hour request (or its category request when it has none).</summary>
    public static string ImplementationStatus(ScenarioRow r, bool hasAfter, Proposal? p = null)
    {
        p ??= r.Proposal ?? r.CategoryRequest;
        if (p is null) return "No request";
        if (p.Status is ProposalStatus.Submitted) return "Awaiting decision";
        if (p.Status is ProposalStatus.Returned) return "Returned to department";
        if (p.Status is ProposalStatus.Rejected) return "Rejected";
        if (p.Kind == ProposalKind.CategoryChange)
        {
            if (!hasAfter) return p.ImplementedAt is null ? "Approved - move the category in Primavera" : "Marked implemented - awaiting extract";
            return r.CategoryIn(ExportService.AfterImplementation) == p.RequestedCategory ? "Implemented" : "Not implemented";
        }
        if (r.Change.MonthsChanged.Count == 0) return "Code or category change - confirm in Primavera";
        if (!hasAfter) return p.ImplementedAt is null ? "Approved - to implement" : "Marked implemented - awaiting extract";
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
            var proj = s.Projects.GetValueOrDefault(g.Key) ?? s.MasterOf(g.Key);
            var name = proj?.Name ?? g.Select(r => r.Activity).OrderByDescending(a => a.Length).FirstOrDefault() ?? "";
            var types = string.Join("; ", concern.SelectMany(r => r.Types).Distinct());
            result.Add(new ProjectSeverity(g.Key, name, proj?.Client, pts, level, depts.Count, string.Join(", ", depts), planning, disc,
                                           inc, red, months, hasNew, hasExt, types, s.MasterOf(g.Key) is not null));
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
        bool IsNew(ScenarioRow r) => r.Proposal?.Kind == ProposalKind.NewAllocation;
        bool IsRemoved(ScenarioRow r) => r.Change.Types.Contains("Removed Allocation");
        var other = concern.Where(r => !IsNew(r) && !IsRemoved(r)).ToList();
        double Total(Func<ScenarioRow, double[]> f) => s.Rows.Sum(r => f(r).Sum());
        return new
        {
            cycle = s.Cycle,
            kpis = new
            {
                planning = Total(r => r.Planning), discipline = Total(r => r.Discipline), final = Total(r => r.Final), after = Total(r => r.After),
                grossIncrease = concern.Sum(r => r.Change.GrossIncrease), grossReduction = concern.Sum(r => r.Change.GrossReduction),
                concernItems = concern.Count, requested,
                implementationRate = requested > 0 ? concern.Sum(r => ImplementedShare(r) * r.Change.GrossChange) / requested : 0,
                newAllocations = concern.Count(IsNew),
                pendingDecisions = s.Rows.SelectMany(r => r.Requests).Count(p => p.Status == ProposalStatus.Submitted),
                critical = projects.Count(p => p.Level == "Critical"), high = projects.Count(p => p.Level == "High"),
                departmentsSubmitted = s.Reviews.Values.Count(r => r.Status is DeptReviewStatus.Submitted or DeptReviewStatus.NoChangeConfirmed),
                departments = depts.Count, capacityPerMonth = totalCap,
            },
            bridge = new
            {
                newWork = concern.Where(IsNew).Sum(r => r.Change.Net),
                increase = other.Sum(r => r.Change.GrossIncrease - r.Change.ExtensionMH - r.Change.EarlyStartMH),
                extension = other.Sum(r => r.Change.ExtensionMH + r.Change.EarlyStartMH),
                reduction = other.Sum(r => r.Change.GrossReduction),
                removed = concern.Where(IsRemoved).Sum(r => r.Change.Net),
            },
            monthly,
            departments = depts,
            topProjects = projects.Where(p => p.Level != "No Concern").Take(10),
        };
    }

    /// <summary>One register entry per request: a man-hour change and a category move on the same row are listed (and
    /// decided) separately.</summary>
    public static IEnumerable<DeviationItem> DeviationRegister(CycleSnapshot s) =>
        s.Rows.Where(r => r.Concern)
            .OrderBy(r => r.DeptCode).ThenByDescending(r => Math.Abs(r.Change.Net))
            .SelectMany(r => r.Requests.Select(p =>
            {
                bool move = p.Kind == ProposalKind.CategoryChange;
                return new DeviationItem(
                    p.Id, r.LineId, r.DeptCode, r.SectionCode, Categories.Label(r.Category), r.ProjectCode, r.Activity,
                    p.Kind.ToString(), p.Status.ToString(), p.Reason, r.Planning.Sum(), r.Discipline.Sum(), r.Final.Sum(), r.After.Sum(),
                    move ? 0 : r.Change.GrossIncrease, move ? 0 : r.Change.GrossReduction, move ? 0 : r.Change.Net,
                    move ? "Phase Transfer" : r.Change.TypesText, move ? "Phase Transfer" : r.Change.BriefCategories,
                    move ? "" : string.Join(", ", r.Change.MonthsChanged.Select(m => s.Cycle.Month(m).ToString("MMM-yy"))),
                    p.RequestedCategory is { } c ? Categories.Label(c) : null, ImplementationStatus(r, s.Cycle.HasAfterData, p),
                    p.ReviewedBy, p.ReviewComment);
            }));
}
