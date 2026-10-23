using System.Text.Json;
using Enppi.Workload.Api.Auth;
using Enppi.Workload.Api.Data;
using Enppi.Workload.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Enppi.Workload.Api.Services;

/// <summary>A request that breaks a business rule (400).</summary>
public class RuleException(string message) : Exception(message);

/// <summary>A request outside the user's role or scope (403).</summary>
public class ForbiddenException(string message) : Exception(message);

public record MonthValue(int MonthIndex, double? Value);
public record ProposalInput(int? LineId, string DeptCode, ProposalKind Kind, string Reason, List<MonthValue>? Values,
                            Category? RequestedCategory = null, string? NewProjectCode = null, string? NewActivity = null,
                            Category? NewCategory = null, string? NewSectionCode = null, int? ProposalId = null);
public record DecisionInput(string Action, string? Comment, List<MonthValue>? ApprovedValues);
public record SaveProposalResult(Proposal Proposal, bool Removed);
public record CycleInput(string Name, DateOnly FirstMonth, int MonthCount = 24, DateOnly? PlannerDue = null,
                         DateOnly? ReviewDue = null, DateOnly? ApprovalDue = null);
public record PlannerLineInput(string DeptCode, string? SectionCode, Category Category, string ProjectCode, string Activity,
                               string? PlannerUpn, List<MonthValue> Values);

/// <summary>Every change to forecast data goes through here: the server enforces role, department scope, cycle stage and
/// category rules on each call (not only the screen), and writes an audit entry.</summary>
public class WorkflowService(WorkloadDbContext db, CurrentUser user)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private async Task<ForecastCycle> CycleAsync(int id) =>
        await db.Cycles.FirstOrDefaultAsync(c => c.Id == id) ?? throw new KeyNotFoundException($"Cycle {id} not found");

    private void RequireCorporatePlanning()
    {
        if (!user.IsCorporatePlanning) throw new ForbiddenException("Only Corporate Planning can do this.");
    }

    private static void RequireOpen(ForecastCycle c)
    {
        if (c.Status == CycleStatus.Locked) throw new RuleException($"Cycle {c.Name} is locked.");
    }

    /// <summary>Trimmed free text, refused when longer than the database column.</summary>
    public static string Text(string? text, int max, string what)
    {
        var t = (text ?? "").Trim();
        if (t.Length > max) throw new RuleException($"{what} is limited to {max:N0} characters ({t.Length:N0} given).");
        return t;
    }

    /// <summary>Month values: inside the cycle, each month once, finite, non-negative and plausible. Null = none.</summary>
    public static List<MonthValue> CheckValues(List<MonthValue>? values, ForecastCycle c)
    {
        var list = values ?? new();
        var seen = new HashSet<int>();
        foreach (var v in list)
        {
            if (v is null) throw new RuleException("A month value is empty.");
            if (v.MonthIndex < 0 || v.MonthIndex >= c.MonthCount) throw new RuleException($"Month {v.MonthIndex} is outside the cycle.");
            if (!seen.Add(v.MonthIndex)) throw new RuleException($"{c.Month(v.MonthIndex):MMM-yy} is given twice.");
            if (Rules.InvalidMH(v.Value) is { } why) throw new RuleException($"{c.Month(v.MonthIndex):MMM-yy}: {why}");
        }
        return list;
    }

    public static void CheckDefined<T>(T? value, string what) where T : struct, Enum
    {
        if (value is { } v && !Enum.IsDefined(v)) throw new RuleException($"Unknown {what} '{v}'.");
    }

    private static bool IsApproved(Proposal p) => p.Status is ProposalStatus.Approved or ProposalStatus.PartiallyApproved;
    private static bool IsDecided(Proposal p) => p.Status is ProposalStatus.Approved or ProposalStatus.PartiallyApproved or ProposalStatus.Rejected;

    // ------------------------------------------------------------------ cycles
    public async Task<ForecastCycle> CreateCycleAsync(CycleInput input)
    {
        RequireCorporatePlanning();
        var name = Text(input.Name, 40, "The cycle name");
        if (name.Length == 0) throw new RuleException("A cycle needs a name, e.g. 'Q4 2026'.");
        if (input.MonthCount is < 1 or > 60) throw new RuleException("A cycle covers 1 to 60 months.");
        if (await db.Cycles.AnyAsync(c => c.Name == name)) throw new RuleException($"Cycle '{name}' already exists.");
        var c = new ForecastCycle
        {
            Name = name, FirstMonth = new DateOnly(input.FirstMonth.Year, input.FirstMonth.Month, 1),
            MonthCount = input.MonthCount, PlannerDue = input.PlannerDue, ReviewDue = input.ReviewDue, ApprovalDue = input.ApprovalDue,
            CreatedBy = user.Upn,
        };
        db.Cycles.Add(c);
        await db.SaveChangesAsync();
        foreach (var d in await db.Departments.Where(d => d.IsActive).Select(d => d.Code).ToListAsync())
            db.DepartmentReviews.Add(new DepartmentReview { CycleId = c.Id, DeptCode = d });
        db.Log(user.Upn, "CreateCycle", "Cycle", c.Id.ToString(), c.Id, c.Name);
        await db.SaveChangesAsync();
        return c;
    }

    public async Task<ForecastCycle> SetStatusAsync(int cycleId, CycleStatus status)
    {
        RequireCorporatePlanning();
        CheckDefined<CycleStatus>(status, "stage");
        var c = await CycleAsync(cycleId);
        RequireOpen(c);
        if (status == CycleStatus.Locked)
        {
            var pending = await db.Proposals.CountAsync(p => p.CycleId == cycleId && p.Status == ProposalStatus.Submitted);
            if (pending > 0) throw new RuleException($"{pending} proposals still wait for a decision; decide them before locking.");
            c.LockedAt = DateTime.UtcNow;
            c.LockedBy = user.Upn;
            // freeze capacity: later headcount or settings changes must not rewrite a locked cycle's utilisation
            var heads = await db.Departments.AsNoTracking().ToListAsync();
            c.LockedCapacity = JsonSerializer.Serialize(new LockedCapacity(await db.LoadSettingsAsync(),
                heads.ToDictionary(d => d.Code, d => new[] { d.Permanent, d.Temporary })), Json);
        }
        db.Log(user.Upn, "SetStatus", "Cycle", c.Id.ToString(), c.Id, $"{c.Status} -> {status}");
        c.Status = status;
        await db.SaveChangesAsync();
        return c;
    }

    public async Task UpdateDueDatesAsync(int cycleId, DateOnly? planner, DateOnly? review, DateOnly? approval)
    {
        RequireCorporatePlanning();
        var c = await CycleAsync(cycleId);
        RequireOpen(c);
        (c.PlannerDue, c.ReviewDue, c.ApprovalDue) = (planner, review, approval);
        db.Log(user.Upn, "DueDates", "Cycle", c.Id.ToString(), c.Id, $"{planner} / {review} / {approval}");
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ planner values
    private bool CanEditPlannerValues(ForecastCycle c, ForecastLine l)
    {
        if (c.Status == CycleStatus.Locked) return false;
        if (user.IsCorporatePlanning) return true;   // Corporate Planning also owns Loan To and Internal values
        if (!user.Is(Roles.Planner) || c.Status is not (CycleStatus.Draft or CycleStatus.PlannerEntry)) return false;
        if (Categories.IsCorporateControlled(l.Category)) return false;
        return l.PlannerUpn == null || l.PlannerUpn == user.Upn;
    }

    public async Task SavePlannerValuesAsync(int cycleId, int lineId, List<MonthValue>? values)
    {
        var c = await CycleAsync(cycleId);
        var l = await db.Lines.Include(x => x.Values).FirstOrDefaultAsync(x => x.Id == lineId && x.CycleId == cycleId)
                ?? throw new KeyNotFoundException($"Line {lineId} not found");
        if (!CanEditPlannerValues(c, l))
            throw new ForbiddenException(Categories.IsCorporateControlled(l.Category)
                ? $"{Categories.Label(l.Category)} values are controlled by Corporate Planning."
                : "Planner values can be changed only by the assigned planner during planner entry.");
        var changes = new List<string>();
        foreach (var mv in CheckValues(values, c))
        {
            var v = l.Values.FirstOrDefault(x => x.MonthIndex == mv.MonthIndex);
            if (v is null) l.Values.Add(v = new ForecastValue { LineId = l.Id, MonthIndex = mv.MonthIndex });
            if (v.PlannerMH != mv.Value) changes.Add($"{c.Month(mv.MonthIndex):MMM-yy}: {v.PlannerMH} -> {mv.Value}");
            v.PlannerMH = mv.Value;
        }
        if (changes.Count > 0) db.Log(user.Upn, "PlannerValues", "Line", l.Id.ToString(), cycleId, string.Join("; ", changes));
        await db.SaveChangesAsync();
    }

    public async Task<ForecastLine> AddPlannerLineAsync(int cycleId, PlannerLineInput input)
    {
        var c = await CycleAsync(cycleId);
        RequireOpen(c);
        CheckDefined<Category>(input.Category, "category");
        bool planner = user.Is(Roles.Planner) && (c.Status is CycleStatus.Draft or CycleStatus.PlannerEntry);
        if (!user.IsCorporatePlanning && !planner) throw new ForbiddenException("Only planners (during planner entry) or Corporate Planning add lines.");
        if (!user.IsCorporatePlanning && Categories.IsCorporateControlled(input.Category))
            throw new ForbiddenException($"{Categories.Label(input.Category)} lines are added by Corporate Planning.");
        if (!await db.Departments.AnyAsync(d => d.Code == input.DeptCode)) throw new RuleException($"Unknown department {input.DeptCode}.");
        var code = Text(input.ProjectCode, 20, "The project code");
        var activity = Text(input.Activity, 250, "The activity");
        var values = CheckValues(input.Values, c);
        if (!await db.Projects.AnyAsync(p => p.Code == code))
            throw new RuleException($"Project {code} is not in the project master; ask Corporate Planning to add it.");
        var line = new ForecastLine
        {
            CycleId = cycleId, DeptCode = input.DeptCode, SectionCode = string.IsNullOrWhiteSpace(input.SectionCode) ? input.DeptCode : Text(input.SectionCode, 8, "The sub-department"),
            Category = input.Category, ProjectCode = code, Activity = activity, Origin = LineOrigin.Planner,
            PlannerUpn = user.IsCorporatePlanning ? Text(input.PlannerUpn, 120, "The planner").ToLowerInvariant() is { Length: > 0 } pu ? pu : null : user.Upn,
            Values = values.Where(v => v.Value != null).Select(v => new ForecastValue { MonthIndex = v.MonthIndex, PlannerMH = v.Value }).ToList(),
        };
        db.Lines.Add(line);
        db.Log(user.Upn, "AddLine", "Line", code, cycleId, $"{input.DeptCode} {input.Category} {code}");
        await db.SaveChangesAsync();
        return line;
    }

    // ------------------------------------------------------------------ department proposals
    private async Task<(ForecastCycle Cycle, DepartmentReview Review)> ReviewWindowAsync(int cycleId, string dept)
    {
        var c = await CycleAsync(cycleId);
        RequireOpen(c);
        if (!user.CanReview(dept)) throw new ForbiddenException($"You cannot review department {dept}.");
        var rev = await db.DepartmentReviews.FirstOrDefaultAsync(r => r.CycleId == cycleId && r.DeptCode == dept)
                  ?? db.DepartmentReviews.Add(new DepartmentReview { CycleId = cycleId, DeptCode = dept }).Entity;
        bool open = c.Status == CycleStatus.DisciplineReview ||
                    (c.Status == CycleStatus.Approval && rev.Status == DeptReviewStatus.Returned) || user.IsCorporatePlanning;
        if (!open) throw new RuleException("Department review is not open for this cycle.");
        if (!user.IsCorporatePlanning && rev.Status is DeptReviewStatus.Submitted or DeptReviewStatus.NoChangeConfirmed)
            throw new RuleException("This department has already submitted; Corporate Planning can return it for changes.");
        return (c, rev);
    }

    public async Task<SaveProposalResult> SaveProposalAsync(int cycleId, ProposalInput input)
    {
        CheckDefined<ProposalKind>(input.Kind, "request kind");
        CheckDefined(input.RequestedCategory, "category");
        CheckDefined(input.NewCategory, "category");
        var (c, rev) = await ReviewWindowAsync(cycleId, input.DeptCode);
        var values = CheckValues(input.Values, c);
        var reason = Text(input.Reason, 2000, "The reason");
        Proposal? p;
        ForecastLine? line = null;
        const string decided = "This request has been decided; Corporate Planning returns it to the department before it can change.";
        if (input.Kind == ProposalKind.NewAllocation)
        {
            // an unknown code is only recorded on the request; the project row is created when Corporate Planning approves it
            var code = Text(input.NewProjectCode, 20, "The project code");
            if (code.Length == 0) throw new RuleException("Choose the project code from the master or request a new code.");
            var section = string.IsNullOrWhiteSpace(input.NewSectionCode) ? input.DeptCode : Text(input.NewSectionCode, 8, "The sub-department");
            var category = input.NewCategory ?? Category.Proposal;
            if (Categories.IsCorporateControlled(category))
                throw new RuleException($"New {Categories.Label(category)} allocations are raised by Corporate Planning.");
            p = input.ProposalId is null ? null : await db.Proposals.Include(x => x.Values)
                .FirstOrDefaultAsync(x => x.Id == input.ProposalId && x.CycleId == cycleId && x.Kind == ProposalKind.NewAllocation &&
                                          x.DeptCode == input.DeptCode);
            if (p is not null && IsDecided(p)) throw new RuleException(decided);
            if (p is null)
            {
                p = new Proposal { CycleId = cycleId, DeptCode = input.DeptCode, Kind = ProposalKind.NewAllocation, CreatedBy = user.Upn };
                db.Proposals.Add(p);
            }
            if (p.LineId is int linked && await db.Lines.FirstOrDefaultAsync(l => l.Id == linked) is { } ll &&
                (ll.ProjectCode != code || ll.SectionCode != section || ll.Category != category))
                p.LineId = null;   // the request moved to another key: the after-extract line it was matched to no longer applies
            p.NewProjectCode = code;
            p.NewActivity = Text(input.NewActivity, 250, "The activity");
            p.NewCategory = category;
            p.NewSectionCode = section;
        }
        else
        {
            line = await db.Lines.Include(l => l.Values).FirstOrDefaultAsync(l => l.Id == input.LineId && l.CycleId == cycleId)
                   ?? throw new KeyNotFoundException($"Line {input.LineId} not found");
            if (line.DeptCode != input.DeptCode) throw new ForbiddenException("The line belongs to another department.");
            if (Categories.IsCorporateControlled(line.Category) && !user.IsCorporatePlanning)
                throw new RuleException($"{Categories.Label(line.Category)} is Corporate Planning controlled and read-only here.");
            if (input.Kind == ProposalKind.CategoryChange)
            {
                if (input.RequestedCategory is null || input.RequestedCategory == line.Category)
                    throw new RuleException("Choose the category the project should move to.");
                if (Categories.IsCorporateControlled(input.RequestedCategory.Value) && !user.IsCorporatePlanning)
                    throw new RuleException($"Moves into {Categories.Label(input.RequestedCategory.Value)} are made by Corporate Planning.");
            }
            // one proposal of each kind per line; a decided one changes only after Corporate Planning returns it
            p = await db.Proposals.Include(x => x.Values)
                .FirstOrDefaultAsync(x => x.CycleId == cycleId && x.LineId == line.Id && x.Kind == input.Kind);
            if (p is not null && IsDecided(p)) throw new RuleException(decided);
            if (p is null)
            {
                p = new Proposal { CycleId = cycleId, LineId = line.Id, DeptCode = line.DeptCode, Kind = input.Kind, CreatedBy = user.Upn };
                db.Proposals.Add(p);
            }
            p.RequestedCategory = input.Kind == ProposalKind.CategoryChange ? input.RequestedCategory : null;
        }
        // values: null = blank = keep planner value
        p.Values.Clear();
        if (p.Kind != ProposalKind.CategoryChange)
            foreach (var v in values.Where(v => v.Value != null))
                p.Values.Add(new ProposalValue { MonthIndex = v.MonthIndex, ProposedMH = Math.Round(v.Value!.Value, 2) });
        double[] planner = new double[c.MonthCount];
        if (line is not null)
            foreach (var v in line.Values) if (v.MonthIndex < c.MonthCount) planner[v.MonthIndex] = v.PlannerMH ?? 0;
        var proposed = Enumerable.Range(0, c.MonthCount).Select(m => p.Values.FirstOrDefault(x => x.MonthIndex == m)?.ProposedMH).ToArray();
        var change = Rules.Classify(planner, proposed, p.Kind,
                                    p.Kind == ProposalKind.NewAllocation ? await NewAllocationTypeAsync(cycleId, p) : null);
        if (p.Kind == ProposalKind.Change && change.MonthsChanged.Count == 0)
        {
            // nothing differs from the planner value: drop the request instead of storing a no-op
            bool existed = p.Id != 0;
            if (existed) db.Proposals.Remove(p);
            else db.Entry(p).State = EntityState.Detached;
            if (existed) db.Log(user.Upn, "WithdrawProposal", "Proposal", p.Id.ToString(), cycleId, "values equal the planner values");
            await db.SaveChangesAsync();
            return new SaveProposalResult(p, existed);
        }
        p.Reason = reason;
        p.ChangeTypes = change.TypesText;
        if ((p.Status is ProposalStatus.Returned or ProposalStatus.Submitted) && !user.IsCorporatePlanning) p.Status = ProposalStatus.Draft;
        if (rev.Status == DeptReviewStatus.NotStarted) rev.Status = DeptReviewStatus.InProgress;
        db.Log(user.Upn, "SaveProposal", "Proposal", p.Id.ToString(), cycleId,
               JsonSerializer.Serialize(new { input.DeptCode, input.LineId, p.Kind, change.TypesText, change.Net, p.Reason }, Json));
        await db.SaveChangesAsync();
        return new SaveProposalResult(p, false);
    }

    /// <summary>New Project, New Phase Allocation or a reused code, judged against the department's Planning Input lines
    /// (as the engine does), not against lines that exist only in the after extract.</summary>
    public async Task<string> NewAllocationTypeAsync(int cycleId, Proposal p)
    {
        var code = p.NewProjectCode ?? "";
        var baseCode = Rules.BaseCode(code);
        var lines = await db.Lines.AsNoTracking().Include(l => l.Values)
            .Where(l => l.CycleId == cycleId && l.DeptCode == p.DeptCode && l.Origin != LineOrigin.DepartmentRequest && l.Id != (p.LineId ?? 0))
            .ToListAsync();
        var baseline = lines.Where(WorkloadService.InBaseline).ToList();
        var section = p.NewSectionCode ?? p.DeptCode;
        var category = p.NewCategory ?? Category.Proposal;
        bool earlier = await db.Proposals.AnyAsync(x => x.CycleId == cycleId && x.Kind == ProposalKind.NewAllocation && x.DeptCode == p.DeptCode &&
                                                        x.NewProjectCode == code && (x.NewSectionCode ?? x.DeptCode) == section &&
                                                        x.NewCategory == category && x.Id != p.Id && (p.Id == 0 || x.Id < p.Id));
        return Rules.NewAllocationType(baseline.Any(l => Rules.BaseCode(l.ProjectCode) == baseCode),
                                       earlier || baseline.Any(l => l.ProjectCode == code && l.SectionCode == section && l.Category == category));
    }

    public async Task DeleteDraftAsync(int cycleId, int proposalId)
    {
        var p = await db.Proposals.FirstOrDefaultAsync(x => x.Id == proposalId && x.CycleId == cycleId)
                ?? throw new KeyNotFoundException($"Proposal {proposalId} not found");
        await ReviewWindowAsync(cycleId, p.DeptCode);
        if (p.Status is not (ProposalStatus.Draft or ProposalStatus.Returned)) throw new RuleException("Only draft or returned proposals can be withdrawn.");
        db.Proposals.Remove(p);
        db.Log(user.Upn, "WithdrawProposal", "Proposal", p.Id.ToString(), cycleId);
        await db.SaveChangesAsync();
    }

    /// <summary>Submit the department's review. Every change needs a reason; large changes need a fuller one.
    /// Submitting with no proposals records an explicit "no change" confirmation.</summary>
    public async Task<DepartmentReview> SubmitDepartmentAsync(int cycleId, string dept)
    {
        var (c, rev) = await ReviewWindowAsync(cycleId, dept);
        var settings = await db.LoadSettingsAsync();
        var drafts = await db.Proposals.Include(p => p.Values).Include(p => p.Line).ThenInclude(l => l!.Values)
            .Where(p => p.CycleId == cycleId && p.DeptCode == dept && (p.Status == ProposalStatus.Draft || p.Status == ProposalStatus.Returned))
            .ToListAsync();
        var problems = new List<string>();
        foreach (var p in drafts)
        {
            var planner = new double[c.MonthCount];
            if (p.Line is not null && p.Kind != ProposalKind.NewAllocation)
                foreach (var v in p.Line.Values) if (v.MonthIndex < c.MonthCount) planner[v.MonthIndex] = v.PlannerMH ?? 0;
            var proposed = Enumerable.Range(0, c.MonthCount).Select(m => p.Values.FirstOrDefault(x => x.MonthIndex == m)?.ProposedMH).ToArray();
            var change = Rules.Classify(planner, proposed, p.Kind);
            var label = p.Kind == ProposalKind.NewAllocation ? p.NewProjectCode : p.Line?.ProjectCode ?? $"#{p.Id}";
            if (string.IsNullOrWhiteSpace(p.Reason)) problems.Add($"{label}: a reason is required");
            else if (Rules.NeedsJustification(change, planner.Sum(), settings) && p.Reason.Trim().Length < settings.JustifyMinChars)
                problems.Add($"{label}: a change of {change.GrossChange:N0} MH needs a fuller justification (at least {settings.JustifyMinChars} characters)");
        }
        if (problems.Count > 0) throw new RuleException("Cannot submit: " + string.Join("; ", problems));
        foreach (var p in drafts)
        {
            p.Status = ProposalStatus.Submitted;
            p.SubmittedAt = DateTime.UtcNow;
        }
        var submittedBefore = await db.Proposals.AnyAsync(p => p.CycleId == cycleId && p.DeptCode == dept && p.Status != ProposalStatus.Draft);
        rev.Status = drafts.Count == 0 && !submittedBefore ? DeptReviewStatus.NoChangeConfirmed : DeptReviewStatus.Submitted;
        rev.SubmittedAt = DateTime.UtcNow;
        rev.SubmittedBy = user.Upn;
        db.Log(user.Upn, "SubmitReview", "Department", dept, cycleId, $"{drafts.Count} proposals; {rev.Status}");
        await db.SaveChangesAsync();
        return rev;
    }

    public async Task<DepartmentReview> ReturnDepartmentAsync(int cycleId, string dept, string? comment)
    {
        RequireCorporatePlanning();
        var c = await CycleAsync(cycleId);
        RequireOpen(c);
        var note = Text(comment, 500, "The note to the department");
        if (note.Length == 0) throw new RuleException("Tell the department what to change.");
        var rev = await db.DepartmentReviews.FirstOrDefaultAsync(r => r.CycleId == cycleId && r.DeptCode == dept)
                  ?? throw new KeyNotFoundException($"Department {dept} not in cycle");
        rev.Status = DeptReviewStatus.Returned;
        rev.Note = note;
        db.Log(user.Upn, "ReturnReview", "Department", dept, cycleId, note);
        await db.SaveChangesAsync();
        return rev;
    }

    // ------------------------------------------------------------------ Corporate Planning decisions
    /// <summary>Approve (as proposed), approve partly (Corporate Planning's own values), return to the department or reject.
    /// A decided request can be decided again; the line's approved values are always rebuilt from the requests that are
    /// approved now, so an earlier decision never lingers.</summary>
    public async Task<Proposal> DecideAsync(int proposalId, DecisionInput input)
    {
        RequireCorporatePlanning();
        var p = await db.Proposals.Include(x => x.Values).FirstOrDefaultAsync(x => x.Id == proposalId)
                ?? throw new KeyNotFoundException($"Proposal {proposalId} not found");
        var c = await CycleAsync(p.CycleId);
        RequireOpen(c);
        if (p.Status is ProposalStatus.Draft or ProposalStatus.Returned)
            throw new RuleException($"Proposal is {p.Status}; the department submits it before it can be decided.");
        var action = (input.Action ?? "").Trim().ToLowerInvariant();
        var comment = Text(input.Comment, 2000, "The comment");
        if ((action is "return" or "reject") && comment.Length == 0)
            throw new RuleException("Give the department a reason when returning or rejecting.");
        int? lineBefore = p.LineId;
        switch (action)
        {
            case "approve":
            case "partial":
                SetApproval(p, c, action == "partial" && p.Kind != ProposalKind.CategoryChange ? CheckValues(input.ApprovedValues, c) : null);
                if (p.Kind == ProposalKind.NewAllocation) await LinkNewAllocationAsync(p);
                break;
            case "return":
                ClearApproval(p);
                p.Status = ProposalStatus.Returned;
                var rev = await db.DepartmentReviews.FirstOrDefaultAsync(r => r.CycleId == p.CycleId && r.DeptCode == p.DeptCode);
                if (rev is not null) rev.Status = DeptReviewStatus.Returned;
                break;
            case "reject":
                ClearApproval(p);
                p.Status = ProposalStatus.Rejected;
                break;
            default:
                throw new RuleException("Action must be approve, partial, return or reject.");
        }
        p.ReviewedBy = user.Upn;
        p.ReviewedAt = DateTime.UtcNow;
        p.ReviewComment = comment.Length > 0 ? comment : null;
        if (p.LineId is int lineId) await SyncLineAsync(lineId, p);
        if (lineBefore is int old && old != p.LineId) await SyncLineAsync(old, p);
        if (p.Kind == ProposalKind.NewAllocation && action is "return" or "reject") await DropEmptyRequestLineAsync(p);
        db.Log(user.Upn, "Decision", "Proposal", p.Id.ToString(), p.CycleId, $"{action}: {comment}");
        await db.SaveChangesAsync();
        return p;
    }

    /// <summary>Approved value per month: the proposal's value, or Corporate Planning's for a partial approval (a blank
    /// month there means the planner value stands). Any earlier decision is cleared first.</summary>
    private static void SetApproval(Proposal p, ForecastCycle c, List<MonthValue>? partial)
    {
        ClearApproval(p);
        var approved = p.Values.ToDictionary(v => v.MonthIndex, v => v.ProposedMH);
        if (partial is not null)
            foreach (var mv in partial) approved[mv.MonthIndex] = mv.Value is { } x ? Math.Round(x, 2) : null;
        foreach (var v in p.Values) v.ApprovedMH = approved.GetValueOrDefault(v.MonthIndex);
        foreach (var (m, val) in approved.Where(kv => kv.Value != null && p.Values.All(v => v.MonthIndex != kv.Key)))
            p.Values.Add(new ProposalValue { MonthIndex = m, ProposedMH = null, ApprovedMH = val });
        bool same = p.Values.All(v => v.ApprovedMH == v.ProposedMH);
        p.Status = same ? ProposalStatus.Approved : ProposalStatus.PartiallyApproved;
    }

    private static void ClearApproval(Proposal p)
    {
        foreach (var v in p.Values.Where(v => v.ProposedMH is null).ToList()) p.Values.Remove(v);   // months only Corporate Planning set
        foreach (var v in p.Values) v.ApprovedMH = null;
    }

    /// <summary>An approved new allocation gets its own line: the after-extract line with the same key when the planner
    /// already carried the work into Primavera, otherwise a new one. The project row is created now if the code is new.</summary>
    private async Task LinkNewAllocationAsync(Proposal p)
    {
        if (p.LineId is not null) return;
        var code = p.NewProjectCode!;
        var section = p.NewSectionCode ?? p.DeptCode;
        var category = p.NewCategory ?? Category.Proposal;
        var candidates = await db.Lines.Include(l => l.Values)
            .Where(l => l.CycleId == p.CycleId && l.DeptCode == p.DeptCode && l.SectionCode == section && l.Category == category && l.ProjectCode == code)
            .ToListAsync();
        ForecastLine? line = null;
        foreach (var l in candidates.Where(l => !WorkloadService.InBaseline(l)))
            if (!await db.Proposals.AnyAsync(x => x.LineId == l.Id && x.Id != p.Id)) { line = l; break; }
        if (line is null)
        {
            if (!await db.Projects.AnyAsync(x => x.Code == code))
                db.Projects.Add(await ProjectCatalog.NewProjectAsync(db, code, p.NewActivity));
            line = new ForecastLine
            {
                CycleId = p.CycleId, DeptCode = p.DeptCode, SectionCode = section, Category = category, ProjectCode = code,
                Activity = p.NewActivity ?? "", Origin = LineOrigin.DepartmentRequest,
            };
            db.Lines.Add(line);
            await db.SaveChangesAsync();
        }
        else line.Origin = LineOrigin.DepartmentRequest;
        p.LineId = line.Id;
    }

    /// <summary>A returned or rejected new allocation goes back to being a request only, unless its line also carries
    /// after-extract values.</summary>
    private async Task DropEmptyRequestLineAsync(Proposal p)
    {
        if (p.LineId is not int id) return;
        var line = await db.Lines.Include(l => l.Values).FirstOrDefaultAsync(l => l.Id == id);
        if (line is null || line.Origin != LineOrigin.DepartmentRequest || InUse(line)) return;
        if (await db.Proposals.AnyAsync(x => x.LineId == id && x.Id != p.Id)) return;
        p.LineId = null;
        await db.SaveChangesAsync();
        db.Lines.Remove(line);
        static bool InUse(ForecastLine l) => l.Values.Any(v => v.PlannerMH != null || v.AfterMH != null);
    }

    /// <summary>Rebuilds a line's approved values and approved category from the requests on it that are approved now.</summary>
    private async Task SyncLineAsync(int lineId, Proposal current)
    {
        var line = await db.Lines.Include(l => l.Values).FirstOrDefaultAsync(l => l.Id == lineId);
        if (line is null) return;
        var onLine = await db.Proposals.Include(x => x.Values).Where(x => x.LineId == lineId).ToListAsync();
        if (current.LineId == lineId && !onLine.Contains(current)) onLine.Add(current);
        onLine.RemoveAll(x => x.LineId != lineId);   // in-memory state wins over the stored one
        ApplyApprovals(line, onLine);
    }

    public static void ApplyApprovals(ForecastLine line, IEnumerable<Proposal> onLine)
    {
        foreach (var v in line.Values) v.ApprovedMH = null;
        line.ApprovedCategory = null;
        foreach (var q in onLine.Where(IsApproved).OrderBy(q => q.ReviewedAt))
        {
            if (q.Kind == ProposalKind.CategoryChange)
            {
                line.ApprovedCategory = q.RequestedCategory == line.Category ? null : q.RequestedCategory;
                continue;
            }
            foreach (var pv in q.Values.Where(v => v.ApprovedMH != null))
            {
                var fv = line.Values.FirstOrDefault(v => v.MonthIndex == pv.MonthIndex);
                if (fv is null) line.Values.Add(fv = new ForecastValue { LineId = line.Id, MonthIndex = pv.MonthIndex });
                fv.ApprovedMH = pv.ApprovedMH;
            }
        }
    }

    /// <summary>Approve every submitted change on projects whose total requested change is small (below both thresholds).</summary>
    public async Task<int> BulkApproveAsync(int cycleId)
    {
        RequireCorporatePlanning();
        var c = await CycleAsync(cycleId);
        RequireOpen(c);
        var settings = await db.LoadSettingsAsync();
        var lines = await db.Lines.AsNoTracking().Include(l => l.Values).Where(l => l.CycleId == cycleId).ToListAsync();
        var proposals = await db.Proposals.AsNoTracking().Include(p => p.Values)
            .Where(p => p.CycleId == cycleId && p.Status != ProposalStatus.Draft).ToListAsync();
        var rows = WorkloadService.BuildRows(c, lines, proposals);
        var small = rows.GroupBy(r => r.ProjectCode)
            .Where(g => g.All(r => r.Proposal?.Kind != ProposalKind.NewAllocation && r.CategoryRequest is null) &&
                        Rules.IsBulkApprovable(g.Where(r => r.Concern).Sum(r => r.Change.GrossChange), g.Sum(r => r.Planning.Sum()), settings))
            .SelectMany(g => g.Where(r => r.Proposal?.Status == ProposalStatus.Submitted).Select(r => r.Proposal!.Id)).ToList();
        foreach (var id in small)
            await DecideAsync(id, new DecisionInput("approve", $"Bulk approved: project change below {settings.BulkMaxPct:P0} and {settings.BulkMaxMH:N0} MH", null));
        return small.Count;
    }

    // ------------------------------------------------------------------ implementation
    public async Task MarkImplementedAsync(int proposalId)
    {
        var p = await db.Proposals.Include(x => x.Line).FirstOrDefaultAsync(x => x.Id == proposalId)
                ?? throw new KeyNotFoundException($"Proposal {proposalId} not found");
        var c = await CycleAsync(p.CycleId);
        RequireOpen(c);
        bool mine = user.Is(Roles.Planner) && (p.Line?.PlannerUpn == null || p.Line.PlannerUpn == user.Upn);
        if (!user.IsCorporatePlanning && !mine) throw new ForbiddenException("Only the project's planner marks implementation.");
        if (!IsApproved(p)) throw new RuleException("Only approved changes are implemented.");
        p.ImplementedAt = DateTime.UtcNow;
        p.ImplementedBy = user.Upn;
        db.Log(user.Upn, "MarkImplemented", "Proposal", p.Id.ToString(), p.CycleId);
        await db.SaveChangesAsync();
    }
}

/// <summary>Headcount (permanent, temporary) and settings frozen when a cycle is locked.</summary>
public record LockedCapacity(WorkloadSettings Settings, Dictionary<string, int[]> Heads);

/// <summary>Project rows for codes that are not in the project master yet. A phase of a master project (5248200-1 of
/// 5248200) counts as in the master and takes the base project's name and client, as in the comments analysis.</summary>
public static class ProjectCatalog
{
    public static async Task<Project> NewProjectAsync(WorkloadDbContext db, string code, string? activity)
    {
        var baseCode = Rules.BaseCode(code);
        var master = baseCode == code ? null : await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Code == baseCode && x.InMaster);
        return FromBase(code, activity, master);
    }

    public static Project FromBase(string code, string? activity, Project? master) => new()
    {
        Code = code, BaseCode = Rules.BaseCode(code), Name = master?.Name ?? (string.IsNullOrWhiteSpace(activity) ? code : activity.Trim()),
        Client = master?.Client, MasterCategory = master?.MasterCategory, InMaster = master is not null,
    };
}
