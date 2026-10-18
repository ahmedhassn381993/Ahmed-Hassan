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

    // ------------------------------------------------------------------ cycles
    public async Task<ForecastCycle> CreateCycleAsync(CycleInput input)
    {
        RequireCorporatePlanning();
        if (string.IsNullOrWhiteSpace(input.Name)) throw new RuleException("A cycle needs a name, e.g. 'Q4 2026'.");
        if (input.MonthCount is < 1 or > 60) throw new RuleException("A cycle covers 1 to 60 months.");
        if (await db.Cycles.AnyAsync(c => c.Name == input.Name.Trim())) throw new RuleException($"Cycle '{input.Name}' already exists.");
        var c = new ForecastCycle
        {
            Name = input.Name.Trim(), FirstMonth = new DateOnly(input.FirstMonth.Year, input.FirstMonth.Month, 1),
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
        var c = await CycleAsync(cycleId);
        RequireOpen(c);
        if (status == CycleStatus.Locked)
        {
            var pending = await db.Proposals.CountAsync(p => p.CycleId == cycleId && p.Status == ProposalStatus.Submitted);
            if (pending > 0) throw new RuleException($"{pending} proposals still wait for a decision; decide them before locking.");
            c.LockedAt = DateTime.UtcNow;
            c.LockedBy = user.Upn;
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

    public async Task SavePlannerValuesAsync(int cycleId, int lineId, List<MonthValue> values)
    {
        var c = await CycleAsync(cycleId);
        var l = await db.Lines.Include(x => x.Values).FirstOrDefaultAsync(x => x.Id == lineId && x.CycleId == cycleId)
                ?? throw new KeyNotFoundException($"Line {lineId} not found");
        if (!CanEditPlannerValues(c, l))
            throw new ForbiddenException(Categories.IsCorporateControlled(l.Category)
                ? $"{Categories.Label(l.Category)} values are controlled by Corporate Planning."
                : "Planner values can be changed only by the assigned planner during planner entry.");
        var changes = new List<string>();
        foreach (var mv in values)
        {
            if (mv.MonthIndex < 0 || mv.MonthIndex >= c.MonthCount) throw new RuleException($"Month {mv.MonthIndex} is outside the cycle.");
            if (mv.Value is < 0) throw new RuleException("Man-hours cannot be negative.");
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
        bool planner = user.Is(Roles.Planner) && (c.Status is CycleStatus.Draft or CycleStatus.PlannerEntry);
        if (!user.IsCorporatePlanning && !planner) throw new ForbiddenException("Only planners (during planner entry) or Corporate Planning add lines.");
        if (!user.IsCorporatePlanning && Categories.IsCorporateControlled(input.Category))
            throw new ForbiddenException($"{Categories.Label(input.Category)} lines are added by Corporate Planning.");
        if (!await db.Departments.AnyAsync(d => d.Code == input.DeptCode)) throw new RuleException($"Unknown department {input.DeptCode}.");
        var code = input.ProjectCode.Trim();
        if (input.Values.Any(v => v.MonthIndex < 0 || v.MonthIndex >= c.MonthCount || v.Value is < 0))
            throw new RuleException("Values must be non-negative and inside the cycle's months.");
        if (!await db.Projects.AnyAsync(p => p.Code == code))
            throw new RuleException($"Project {code} is not in the project master; ask Corporate Planning to add it.");
        var line = new ForecastLine
        {
            CycleId = cycleId, DeptCode = input.DeptCode, SectionCode = string.IsNullOrWhiteSpace(input.SectionCode) ? input.DeptCode : input.SectionCode!,
            Category = input.Category, ProjectCode = code, Activity = input.Activity, Origin = LineOrigin.Planner,
            PlannerUpn = user.IsCorporatePlanning ? input.PlannerUpn : user.Upn,
            Values = input.Values.Where(v => v.Value != null).Select(v => new ForecastValue { MonthIndex = v.MonthIndex, PlannerMH = v.Value }).ToList(),
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

    public async Task<Proposal> SaveProposalAsync(int cycleId, ProposalInput input)
    {
        var (c, rev) = await ReviewWindowAsync(cycleId, input.DeptCode);
        var settings = await db.LoadSettingsAsync();
        Proposal? p;
        ForecastLine? line = null;
        if (input.Kind == ProposalKind.NewAllocation)
        {
            var code = (input.NewProjectCode ?? "").Trim();
            if (code.Length == 0) throw new RuleException("Choose the project code from the master or request a new code.");
            if (!await db.Projects.AnyAsync(x => x.Code == code))
            {
                // a code not in the master is recorded as a request; Corporate Planning confirms it on approval
                db.Projects.Add(new Project { Code = code, BaseCode = Rules.BaseCode(code), Name = input.NewActivity ?? code, InMaster = false });
            }
            if (input.NewCategory is { } nc && Categories.IsCorporateControlled(nc))
                throw new RuleException($"New {Categories.Label(nc)} allocations are raised by Corporate Planning.");
            p = input.ProposalId is null ? null : await db.Proposals.Include(x => x.Values)
                .FirstOrDefaultAsync(x => x.Id == input.ProposalId && x.CycleId == cycleId && x.Kind == ProposalKind.NewAllocation &&
                                          x.DeptCode == input.DeptCode);
            if (p is { Status: ProposalStatus.Approved or ProposalStatus.PartiallyApproved or ProposalStatus.Rejected } && !user.IsCorporatePlanning)
                throw new RuleException("This request has been decided; ask Corporate Planning to return it before changing it.");
            if (p is null)
            {
                p = new Proposal { CycleId = cycleId, DeptCode = input.DeptCode, Kind = ProposalKind.NewAllocation, CreatedBy = user.Upn };
                db.Proposals.Add(p);
            }
            p.NewProjectCode = code;
            p.NewActivity = input.NewActivity;
            p.NewCategory = input.NewCategory ?? Category.Proposal;
            p.NewSectionCode = string.IsNullOrWhiteSpace(input.NewSectionCode) ? input.DeptCode : input.NewSectionCode;
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
            // one proposal of each kind per line: a decided one is reopened only by Corporate Planning returning it
            p = await db.Proposals.Include(x => x.Values)
                .FirstOrDefaultAsync(x => x.CycleId == cycleId && x.LineId == line.Id && x.Kind == input.Kind);
            if (p is { Status: ProposalStatus.Approved or ProposalStatus.PartiallyApproved or ProposalStatus.Rejected } && !user.IsCorporatePlanning)
                throw new RuleException("This change has been decided; ask Corporate Planning to return it before changing it.");
            if (p is null)
            {
                p = new Proposal { CycleId = cycleId, LineId = line.Id, DeptCode = line.DeptCode, Kind = input.Kind, CreatedBy = user.Upn };
                db.Proposals.Add(p);
            }
            p.RequestedCategory = input.RequestedCategory;
        }
        // values: null = blank = keep planner value
        var values = (input.Values ?? new()).Where(v => v.MonthIndex >= 0 && v.MonthIndex < c.MonthCount).ToList();
        if (values.Any(v => v.Value is < 0)) throw new RuleException("Man-hours cannot be negative.");
        p.Values.Clear();
        foreach (var v in values.Where(v => v.Value != null))
            p.Values.Add(new ProposalValue { MonthIndex = v.MonthIndex, ProposedMH = Math.Round(v.Value!.Value, 2) });
        double[] planner = new double[c.MonthCount];
        if (line is not null)
            foreach (var v in line.Values) if (v.MonthIndex < c.MonthCount) planner[v.MonthIndex] = v.PlannerMH ?? 0;
        var proposed = Enumerable.Range(0, c.MonthCount).Select(m => p.Values.FirstOrDefault(x => x.MonthIndex == m)?.ProposedMH).ToArray();
        var change = Rules.Classify(planner, proposed, p.Kind);
        if (p.Kind == ProposalKind.Change && change.MonthsChanged.Count == 0)
        {
            // nothing differs from the planner value: drop the draft instead of storing a no-op
            if (p.Id != 0) db.Proposals.Remove(p);
            else db.Entry(p).State = EntityState.Detached;
            await db.SaveChangesAsync();
            return p;
        }
        p.Reason = (input.Reason ?? "").Trim();
        p.ChangeTypes = change.TypesText;
        if ((p.Status is ProposalStatus.Returned or ProposalStatus.Submitted) && !user.IsCorporatePlanning) p.Status = ProposalStatus.Draft;
        if (rev.Status == DeptReviewStatus.NotStarted) rev.Status = DeptReviewStatus.InProgress;
        db.Log(user.Upn, "SaveProposal", "Proposal", p.Id.ToString(), cycleId,
               JsonSerializer.Serialize(new { input.DeptCode, input.LineId, p.Kind, change.TypesText, change.Net, p.Reason }, Json));
        await db.SaveChangesAsync();
        return p;
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
            if (p.Line is not null) foreach (var v in p.Line.Values) if (v.MonthIndex < c.MonthCount) planner[v.MonthIndex] = v.PlannerMH ?? 0;
            var proposed = Enumerable.Range(0, c.MonthCount).Select(m => p.Values.FirstOrDefault(x => x.MonthIndex == m)?.ProposedMH).ToArray();
            var change = Rules.Classify(planner, proposed, p.Kind);
            var label = p.Line?.ProjectCode ?? p.NewProjectCode ?? $"#{p.Id}";
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

    public async Task<DepartmentReview> ReturnDepartmentAsync(int cycleId, string dept, string comment)
    {
        RequireCorporatePlanning();
        var c = await CycleAsync(cycleId);
        RequireOpen(c);
        var rev = await db.DepartmentReviews.FirstOrDefaultAsync(r => r.CycleId == cycleId && r.DeptCode == dept)
                  ?? throw new KeyNotFoundException($"Department {dept} not in cycle");
        rev.Status = DeptReviewStatus.Returned;
        rev.Note = comment;
        db.Log(user.Upn, "ReturnReview", "Department", dept, cycleId, comment);
        await db.SaveChangesAsync();
        return rev;
    }

    // ------------------------------------------------------------------ Corporate Planning decisions
    public async Task<Proposal> DecideAsync(int proposalId, DecisionInput input)
    {
        RequireCorporatePlanning();
        var p = await db.Proposals.Include(x => x.Values).FirstOrDefaultAsync(x => x.Id == proposalId)
                ?? throw new KeyNotFoundException($"Proposal {proposalId} not found");
        var c = await CycleAsync(p.CycleId);
        RequireOpen(c);
        if (p.Status is not (ProposalStatus.Submitted or ProposalStatus.Approved or ProposalStatus.PartiallyApproved))
            throw new RuleException($"Proposal is {p.Status}; only submitted proposals can be decided.");
        var action = (input.Action ?? "").Trim().ToLowerInvariant();
        if ((action is "return" or "reject") && string.IsNullOrWhiteSpace(input.Comment))
            throw new RuleException("Give the department a reason when returning or rejecting.");
        switch (action)
        {
            case "approve":
            case "partial":
                await ApplyApprovalAsync(p, c, action == "partial" ? input.ApprovedValues : null);
                break;
            case "return":
                await RevertApprovalAsync(p);
                p.Status = ProposalStatus.Returned;
                var rev = await db.DepartmentReviews.FirstOrDefaultAsync(r => r.CycleId == p.CycleId && r.DeptCode == p.DeptCode);
                if (rev is not null) rev.Status = DeptReviewStatus.Returned;
                break;
            case "reject":
                await RevertApprovalAsync(p);
                p.Status = ProposalStatus.Rejected;
                break;
            default:
                throw new RuleException("Action must be approve, partial, return or reject.");
        }
        p.ReviewedBy = user.Upn;
        p.ReviewedAt = DateTime.UtcNow;
        p.ReviewComment = input.Comment;
        db.Log(user.Upn, "Decision", "Proposal", p.Id.ToString(), p.CycleId, $"{action}: {input.Comment}");
        await db.SaveChangesAsync();
        return p;
    }

    private async Task ApplyApprovalAsync(Proposal p, ForecastCycle c, List<MonthValue>? partial)
    {
        // approved value per month: the proposal, or Corporate Planning's own figure for a partial approval
        var approved = p.Values.ToDictionary(v => v.MonthIndex, v => v.ProposedMH);
        if (partial is not null)
        {
            if (partial.Any(v => v.Value is < 0)) throw new RuleException("Man-hours cannot be negative.");
            foreach (var mv in partial.Where(v => v.MonthIndex >= 0 && v.MonthIndex < c.MonthCount)) approved[mv.MonthIndex] = mv.Value;
        }
        foreach (var v in p.Values) v.ApprovedMH = approved.GetValueOrDefault(v.MonthIndex);
        foreach (var (m, val) in approved.Where(kv => p.Values.All(v => v.MonthIndex != kv.Key)))
            p.Values.Add(new ProposalValue { MonthIndex = m, ProposedMH = null, ApprovedMH = val });
        bool same = partial is null || p.Values.All(v => v.ApprovedMH == v.ProposedMH);
        p.Status = same ? ProposalStatus.Approved : ProposalStatus.PartiallyApproved;

        ForecastLine line;
        if (p.Kind == ProposalKind.NewAllocation)
        {
            if (p.LineId is int id) line = await db.Lines.Include(l => l.Values).FirstAsync(l => l.Id == id);
            else
            {
                var proj = await db.Projects.FirstOrDefaultAsync(x => x.Code == p.NewProjectCode);
                if (proj is { InMaster: false }) proj.InMaster = true;   // approval confirms the requested code
                line = new ForecastLine
                {
                    CycleId = p.CycleId, DeptCode = p.DeptCode, SectionCode = p.NewSectionCode ?? p.DeptCode,
                    Category = p.NewCategory ?? Category.Proposal, ProjectCode = p.NewProjectCode!, Activity = p.NewActivity ?? "",
                    Origin = LineOrigin.DepartmentRequest,
                };
                db.Lines.Add(line);
                await db.SaveChangesAsync();
                p.LineId = line.Id;
            }
        }
        else
        {
            line = await db.Lines.Include(l => l.Values).FirstAsync(l => l.Id == p.LineId);
            if (p.Kind == ProposalKind.CategoryChange && p.RequestedCategory is { } cat)
            {
                db.Log(user.Upn, "CategoryChange", "Line", line.Id.ToString(), p.CycleId, $"{line.Category} -> {cat}");
                line.Category = cat;
                return;
            }
        }
        foreach (var (m, val) in approved)
        {
            if (val is null) continue;   // blank: planner value stands
            var fv = line.Values.FirstOrDefault(v => v.MonthIndex == m);
            if (fv is null) line.Values.Add(fv = new ForecastValue { LineId = line.Id, MonthIndex = m });
            fv.ApprovedMH = val;
        }
    }

    private async Task RevertApprovalAsync(Proposal p)
    {
        if (p.Status is not (ProposalStatus.Approved or ProposalStatus.PartiallyApproved) || p.LineId is null) return;
        var line = await db.Lines.Include(l => l.Values).FirstAsync(l => l.Id == p.LineId);
        foreach (var v in line.Values) v.ApprovedMH = null;
        foreach (var v in p.Values) v.ApprovedMH = null;
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
            .Where(g => g.All(r => r.Proposal?.Kind != ProposalKind.NewAllocation && r.Proposal?.Kind != ProposalKind.CategoryChange) &&
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
        if (p.Status is not (ProposalStatus.Approved or ProposalStatus.PartiallyApproved)) throw new RuleException("Only approved changes are implemented.");
        p.ImplementedAt = DateTime.UtcNow;
        p.ImplementedBy = user.Upn;
        db.Log(user.Upn, "MarkImplemented", "Proposal", p.Id.ToString(), p.CycleId);
        await db.SaveChangesAsync();
    }
}
