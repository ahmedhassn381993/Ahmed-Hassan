using Enppi.Workload.Api.Auth;
using Enppi.Workload.Api.Data;
using Enppi.Workload.Api.Domain;
using Enppi.Workload.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Enppi.Workload.Api.Api;

public record StatusInput(CycleStatus Status);
public record DueDatesInput(DateOnly? PlannerDue, DateOnly? ReviewDue, DateOnly? ApprovalDue);
public record CommentInput(string Comment);
public record ValuesInput(List<MonthValue> Values);
public record LoginInput(string Upn);
public record UserInput(string Upn, string DisplayName, string Role, string? DeptCodes, string? Email, bool IsActive = true);
public record DepartmentInput(string Name, string? ParentCode, int Permanent, int Temporary, int Managers, int LoanToUnpaid, bool IsActive = true);

public static class Endpoints
{
    private static double?[] Series(ForecastLine l, int n, Func<ForecastValue, double?> f)
    {
        var arr = new double?[n];
        foreach (var v in l.Values) if (v.MonthIndex >= 0 && v.MonthIndex < n) arr[v.MonthIndex] = f(v);
        return arr;
    }

    private static object ProposalDto(Proposal p, int n) => new
    {
        p.Id, p.LineId, p.DeptCode, kind = p.Kind.ToString(), status = p.Status.ToString(), p.Reason, p.ChangeTypes,
        requestedCategory = p.RequestedCategory?.ToString(), p.NewProjectCode, p.NewActivity, newCategory = p.NewCategory?.ToString(),
        p.NewSectionCode, p.CreatedBy, p.SubmittedAt, p.ReviewedBy, p.ReviewedAt, p.ReviewComment, p.ImplementedAt, p.ImplementedBy,
        values = Enumerable.Range(0, n).Select(m => p.Values.FirstOrDefault(v => v.MonthIndex == m)?.ProposedMH).ToArray(),
        approved = Enumerable.Range(0, n).Select(m => p.Values.FirstOrDefault(v => v.MonthIndex == m)?.ApprovedMH).ToArray(),
    };

    private static object LineDto(ForecastLine l, int n, Proposal? change, Proposal? category) => new
    {
        l.Id, dept = l.DeptCode, section = l.SectionCode, category = l.Category.ToString(), categoryLabel = Categories.Label(l.Category),
        approvedCategory = l.ApprovedCategory?.ToString(), afterCategory = l.AfterCategory?.ToString(),
        project = l.ProjectCode, l.Activity, planner = l.PlannerUpn, origin = l.Origin.ToString(),
        corporateControlled = Categories.IsCorporateControlled(l.Category),
        planning = Series(l, n, v => v.PlannerMH), approved = Series(l, n, v => v.ApprovedMH), after = Series(l, n, v => v.AfterMH),
        proposal = change is null ? null : ProposalDto(change, n),
        categoryRequest = category is null ? null : ProposalDto(category, n),
    };

    public static void MapWorkloadApi(this WebApplication app)
    {
        // signed in is not enough: every endpoint needs one of the application roles (Entra app role or Users table)
        var api = app.MapGroup("/api").RequireAuthorization(p => p.RequireRole(Roles.All));
        var cp = Roles.CorporatePlanning;

        // who am I - also answers for a signed-in account without a role, so the screen can say "ask for access"
        app.MapGet("/api/me", (CurrentUser u, IConfiguration cfg) => new
        {
            u.Upn, u.Name, roles = u.RolesList, departments = u.Departments, authMode = cfg["Auth:Mode"],
        }).RequireAuthorization();

        // ------------------------------------------------------------ reference data
        api.MapGet("/departments", async (WorkloadDbContext db) =>
        {
            var s = await db.LoadSettingsAsync();
            return (await db.Departments.AsNoTracking().OrderBy(d => d.Code).ToListAsync())
                .OrderBy(d => int.TryParse(d.Code, out var x) ? x : 0)
                .Select(d => new { d.Code, d.Name, d.ParentCode, d.Permanent, d.Temporary, d.Managers, d.LoanToUnpaid, d.IsActive,
                                   effectiveHeads = Rules.EffectiveHeads(d, s), capacityPerMonth = Rules.CapacityPerMonth(d, s) });
        });
        api.MapGet("/projects", async (WorkloadDbContext db, string? q) =>
        {
            var query = db.Projects.AsNoTracking().Where(p => p.IsActive);
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(p => p.Code.Contains(q) || p.Name.Contains(q));
            return await query.OrderBy(p => p.Code).Take(40).ToListAsync();
        });
        api.MapGet("/settings", async (WorkloadDbContext db) => await db.LoadSettingsAsync());

        // ------------------------------------------------------------ cycles
        api.MapGet("/cycles", async (WorkloadDbContext db) => await db.Cycles.AsNoTracking().OrderByDescending(c => c.FirstMonth).ToListAsync());
        api.MapPost("/cycles", async (CycleInput input, WorkflowService wf) => await wf.CreateCycleAsync(input)).RequireAuthorization(cp);
        api.MapGet("/cycles/{id:int}", async (int id, WorkloadDbContext db) =>
            await db.Cycles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id) is { } c ? Results.Ok(c) : Results.NotFound());
        api.MapPost("/cycles/{id:int}/status", async (int id, StatusInput input, WorkflowService wf) => await wf.SetStatusAsync(id, input.Status))
            .RequireAuthorization(cp);
        api.MapPut("/cycles/{id:int}/due-dates", async (int id, DueDatesInput d, WorkflowService wf) =>
        {
            await wf.UpdateDueDatesAsync(id, d.PlannerDue, d.ReviewDue, d.ApprovalDue);
            return Results.NoContent();
        }).RequireAuthorization(cp);
        api.MapGet("/cycles/{id:int}/dashboard", async (int id, WorkloadService ws, CurrentUser u) =>
            WorkloadService.Dashboard(await ws.SnapshotAsync(id, u.Scope)));

        // ------------------------------------------------------------ lines (planner grid)
        api.MapGet("/cycles/{id:int}/lines", async (int id, string? dept, string? project, bool? mine, WorkloadDbContext db, CurrentUser u) =>
        {
            var c = await db.Cycles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (c is null) return Results.NotFound();
            var q = db.Lines.AsNoTracking().Include(l => l.Values).Where(l => l.CycleId == id);
            if (!string.IsNullOrEmpty(dept)) q = q.Where(l => l.DeptCode == dept);
            if (!string.IsNullOrEmpty(project)) q = q.Where(l => l.ProjectCode == project || l.ProjectCode.StartsWith(project + "-"));
            if (!u.CanSeeAll)
            {
                if (u.Is(Roles.Planner) && (mine ?? true)) q = q.Where(l => l.PlannerUpn == u.Upn);
                else if (!u.Is(Roles.Planner)) { var ds = u.Departments.ToList(); q = q.Where(l => ds.Contains(l.DeptCode)); }
            }
            else if (mine == true) q = q.Where(l => l.PlannerUpn == u.Upn);
            var lines = await q.OrderBy(l => l.ProjectCode).ThenBy(l => l.DeptCode).ToListAsync();
            var ids = lines.Select(l => l.Id).ToList();
            var props = await db.Proposals.AsNoTracking().Include(p => p.Values).Where(p => p.CycleId == id && p.LineId != null && ids.Contains(p.LineId.Value))
                .ToListAsync();
            return Results.Ok(lines.Select(l => LineDto(l, c.MonthCount,
                props.FirstOrDefault(p => p.LineId == l.Id && p.Kind == ProposalKind.Change),
                props.FirstOrDefault(p => p.LineId == l.Id && p.Kind == ProposalKind.CategoryChange))));
        });
        api.MapPut("/cycles/{id:int}/lines/{lineId:int}/planner-values", async (int id, int lineId, ValuesInput input, WorkflowService wf) =>
        {
            await wf.SavePlannerValuesAsync(id, lineId, input.Values);
            return Results.NoContent();
        });
        api.MapPost("/cycles/{id:int}/lines", async (int id, PlannerLineInput input, WorkflowService wf) =>
        {
            var line = await wf.AddPlannerLineAsync(id, input);
            return Results.Ok(new { line.Id });
        });

        // ------------------------------------------------------------ discipline review
        api.MapGet("/cycles/{id:int}/review/{dept}", async (int id, string dept, WorkloadDbContext db, CurrentUser u) =>
        {
            if (!u.CanReview(dept) && !u.CanSeeAll) return Results.Forbid();
            var c = await db.Cycles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (c is null) return Results.NotFound();
            var d = await db.Departments.AsNoTracking().FirstOrDefaultAsync(x => x.Code == dept);
            if (d is null) return Results.NotFound();
            var s = await db.LoadSettingsAsync();
            var props = await db.Proposals.AsNoTracking().Include(p => p.Values).Where(p => p.CycleId == id && p.DeptCode == dept).ToListAsync();
            // the department reviews its Planning Input lines; lines carrying its new-allocation requests are listed with
            // the requests, and lines found only in the after extract are not part of the review
            var requestLines = props.Where(p => p.Kind == ProposalKind.NewAllocation && p.LineId != null).Select(p => p.LineId!.Value).ToHashSet();
            var lines = (await db.Lines.AsNoTracking().Include(l => l.Values).Where(l => l.CycleId == id && l.DeptCode == dept)
                .OrderBy(l => l.Category).ThenBy(l => l.ProjectCode).ToListAsync())
                .Where(l => !requestLines.Contains(l.Id) && l.Origin != LineOrigin.AfterExtract && l.Origin != LineOrigin.DepartmentRequest).ToList();
            var review = await db.DepartmentReviews.AsNoTracking().FirstOrDefaultAsync(r => r.CycleId == id && r.DeptCode == dept);
            return Results.Ok(new
            {
                cycle = c, department = new { d.Code, d.Name, capacityPerMonth = Rules.CapacityPerMonth(d, s), effectiveHeads = Rules.EffectiveHeads(d, s) },
                review = new { status = (review?.Status ?? DeptReviewStatus.NotStarted).ToString(), review?.SubmittedAt, review?.SubmittedBy, review?.Note },
                canEdit = u.CanReview(dept) && c.Status != CycleStatus.Locked &&
                          (u.IsCorporatePlanning || ((c.Status == CycleStatus.DisciplineReview || (c.Status == CycleStatus.Approval &&
                            review?.Status == DeptReviewStatus.Returned)) && review?.Status is not (DeptReviewStatus.Submitted or DeptReviewStatus.NoChangeConfirmed))),
                settings = s,
                lines = lines.Select(l => LineDto(l, c.MonthCount,
                    props.FirstOrDefault(p => p.LineId == l.Id && p.Kind == ProposalKind.Change),
                    props.FirstOrDefault(p => p.LineId == l.Id && p.Kind == ProposalKind.CategoryChange))),
                newAllocations = props.Where(p => p.Kind == ProposalKind.NewAllocation).OrderBy(p => p.Id).Select(p => ProposalDto(p, c.MonthCount)),
            });
        });
        api.MapPost("/cycles/{id:int}/proposals", async (int id, ProposalInput input, WorkflowService wf) =>
        {
            var (p, removed) = await wf.SaveProposalAsync(id, input);
            return Results.Ok(new { id = removed ? 0 : p.Id, status = p.Status.ToString(), p.ChangeTypes, removed });
        });
        api.MapDelete("/cycles/{id:int}/proposals/{pid:int}", async (int id, int pid, WorkflowService wf) =>
        {
            await wf.DeleteDraftAsync(id, pid);
            return Results.NoContent();
        });
        api.MapPost("/cycles/{id:int}/review/{dept}/submit", async (int id, string dept, WorkflowService wf) =>
        {
            var r = await wf.SubmitDepartmentAsync(id, dept);
            return Results.Ok(new { status = r.Status.ToString(), r.SubmittedAt });
        });
        api.MapPost("/cycles/{id:int}/review/{dept}/return", async (int id, string dept, CommentInput input, WorkflowService wf) =>
        {
            var r = await wf.ReturnDepartmentAsync(id, dept, input.Comment);
            return Results.Ok(new { status = r.Status.ToString() });
        }).RequireAuthorization(cp);

        // ------------------------------------------------------------ approval queue
        api.MapGet("/cycles/{id:int}/queue", async (int id, WorkloadService ws) =>
        {
            var s = await ws.SnapshotAsync(id);
            var sev = WorkloadService.Projects(s).ToDictionary(p => p.ProjectCode);
            // one entry per request: a man-hour change and a category move on the same row are decided separately
            return s.Rows.SelectMany(r => r.Requests.Select(q => (r, q)))
                .OrderBy(x => x.q.Status == ProposalStatus.Submitted ? 0 : 1)
                .ThenByDescending(x => sev.TryGetValue(x.r.ProjectCode, out var q) ? q.Points : 0)
                .ThenByDescending(x => Math.Abs(x.r.Change.Net))
                .Select(x =>
            {
                var (r, q) = x;
                bool move = q.Kind == ProposalKind.CategoryChange;
                sev.TryGetValue(r.ProjectCode, out var p);
                return new
                {
                    proposal = ProposalDto(q, s.Cycle.MonthCount), r.LineId, dept = r.DeptCode, section = r.SectionCode,
                    category = Categories.Label(r.Category), project = r.ProjectCode, r.Activity,
                    planning = r.Planning, discipline = r.Discipline, final = r.Final,
                    change = move
                        ? new { types = (IReadOnlyList<string>)new[] { "Phase Transfer" }, GrossIncrease = 0.0, GrossReduction = 0.0, Net = 0.0,
                                ExtensionMH = 0.0, BriefCategories = "Phase Transfer", monthsChanged = (IReadOnlyList<int>)Array.Empty<int>() }
                        : new { types = r.Change.Types, r.Change.GrossIncrease, r.Change.GrossReduction, r.Change.Net, r.Change.ExtensionMH,
                                r.Change.BriefCategories, monthsChanged = r.Change.MonthsChanged },
                    severity = p?.Level ?? "Low", points = p?.Points ?? 0,
                    projectDepartments = p?.Departments, projectName = p?.Name,
                    implementation = WorkloadService.ImplementationStatus(r, s.Cycle.HasAfterData, q),
                };
            });
        }).RequireAuthorization(policy => policy.RequireRole(cp, Roles.Viewer));
        api.MapPost("/proposals/{pid:int}/decision", async (int pid, DecisionInput input, WorkflowService wf) =>
        {
            var p = await wf.DecideAsync(pid, input);
            return Results.Ok(new { p.Id, status = p.Status.ToString() });
        }).RequireAuthorization(cp);
        api.MapPost("/cycles/{id:int}/bulk-approve", async (int id, WorkflowService wf) => new { approved = await wf.BulkApproveAsync(id) })
            .RequireAuthorization(cp);

        // ------------------------------------------------------------ cycle control
        api.MapGet("/cycles/{id:int}/status-board", async (int id, WorkloadDbContext db) =>
        {
            var c = await db.Cycles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (c is null) return Results.NotFound();
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var reviews = await db.DepartmentReviews.AsNoTracking().Where(r => r.CycleId == id).ToDictionaryAsync(r => r.DeptCode);
            var counts = await db.Proposals.AsNoTracking().Where(p => p.CycleId == id)
                .GroupBy(p => new { p.DeptCode, p.Status }).Select(g => new { g.Key.DeptCode, g.Key.Status, n = g.Count() }).ToListAsync();
            var managers = await db.Users.AsNoTracking().Where(u => u.Role == Roles.DisciplineManager && u.IsActive).ToListAsync();
            var depts = (await db.Departments.AsNoTracking().Where(d => d.IsActive).ToListAsync()).OrderBy(d => int.TryParse(d.Code, out var x) ? x : 0);
            return Results.Ok(depts.Select(d =>
            {
                reviews.TryGetValue(d.Code, out var r);
                var st = r?.Status ?? DeptReviewStatus.NotStarted;
                bool done = st is DeptReviewStatus.Submitted or DeptReviewStatus.NoChangeConfirmed;
                return new
                {
                    dept = d.Code, d.Name, status = st.ToString(), r?.SubmittedAt, r?.SubmittedBy, r?.LastReminderAt, reminders = r?.ReminderCount ?? 0,
                    r?.Note, due = c.ReviewDue, overdue = !done && c.ReviewDue is { } due && today > due,
                    drafts = counts.Where(x => x.DeptCode == d.Code && x.Status == ProposalStatus.Draft).Sum(x => x.n),
                    submitted = counts.Where(x => x.DeptCode == d.Code && x.Status != ProposalStatus.Draft).Sum(x => x.n),
                    pending = counts.Where(x => x.DeptCode == d.Code && x.Status == ProposalStatus.Submitted).Sum(x => x.n),
                    managers = managers.Where(m => m.Departments().Contains(d.Code)).Select(m => m.DisplayName),
                };
            }));
        }).RequireAuthorization(policy => policy.RequireRole(cp, Roles.Viewer, Roles.Admin));
        api.MapPost("/cycles/{id:int}/reminders", async (int id, WorkloadDbContext db, INotificationSender sender, CancellationToken ct) =>
        {
            var c = await db.Cycles.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return Results.NotFound();
            if (c.Status is not (CycleStatus.DisciplineReview or CycleStatus.Approval))
                throw new RuleException("Reminders go out while departments can still submit (discipline review, or approval for returned departments).");
            return Results.Ok(new { sent = await ReminderService.SendDueRemindersAsync(db, sender, c, DateOnly.FromDateTime(DateTime.UtcNow), true, ct) });
        }).RequireAuthorization(cp);
        api.MapGet("/cycles/{id:int}/notifications", async (int id, WorkloadDbContext db) =>
            await db.Notifications.AsNoTracking().Where(n => n.CycleId == id).OrderByDescending(n => n.At).Take(200).ToListAsync())
            .RequireAuthorization(policy => policy.RequireRole(cp, Roles.Admin));
        api.MapGet("/cycles/{id:int}/audit", async (int id, WorkloadDbContext db) =>
            await db.Audit.AsNoTracking().Where(a => a.CycleId == id).OrderByDescending(a => a.At).Take(500).ToListAsync())
            .RequireAuthorization(policy => policy.RequireRole(cp, Roles.Admin));

        // ------------------------------------------------------------ reports and implementation
        // a discipline manager's reports cover their departments; Corporate Planning, viewers, administrators and planners see all
        api.MapGet("/cycles/{id:int}/reports/departments", async (int id, WorkloadService ws, CurrentUser u) =>
            WorkloadService.Departments(await ws.SnapshotAsync(id, u.Scope)));
        api.MapGet("/cycles/{id:int}/reports/projects", async (int id, WorkloadService ws, CurrentUser u) =>
            WorkloadService.Projects(await ws.SnapshotAsync(id, u.Scope)));
        api.MapGet("/cycles/{id:int}/reports/deviations", async (int id, WorkloadService ws, CurrentUser u) =>
            WorkloadService.DeviationRegister(await ws.SnapshotAsync(id, u.Scope)));
        api.MapGet("/cycles/{id:int}/implementation", async (int id, WorkloadService ws, CurrentUser u) =>
        {
            var s = await ws.SnapshotAsync(id, u.Scope);
            return s.Rows.SelectMany(r => r.Requests.Where(q => q.Status is ProposalStatus.Approved or ProposalStatus.PartiallyApproved)
                                                    .Select(q => (r, q)))
                .Where(x => u.CanSeeAll || u.Scope is not null || x.r.PlannerUpn == null || x.r.PlannerUpn == u.Upn)
                .Select(x =>
                {
                    var (r, q) = x;
                    bool move = q.Kind == ProposalKind.CategoryChange;
                    return new
                    {
                        proposalId = q.Id, r.LineId, dept = r.DeptCode, category = Categories.Label(r.Category), project = r.ProjectCode,
                        r.Activity, planner = r.PlannerUpn, planning = r.Planning.Sum(), final = r.Final.Sum(), after = r.After.Sum(),
                        types = move ? $"Move to {Categories.Label(q.RequestedCategory ?? r.Category)}" : r.Change.TypesText,
                        status = WorkloadService.ImplementationStatus(r, s.Cycle.HasAfterData, q),
                        implementedAt = q.ImplementedAt, implementedBy = q.ImplementedBy,
                        finalByMonth = r.Final, planningByMonth = r.Planning,
                        monthsChanged = move ? (IReadOnlyList<int>)Array.Empty<int>() : r.Change.MonthsChanged,
                    };
                });
        });
        api.MapPost("/proposals/{pid:int}/implemented", async (int pid, WorkflowService wf) =>
        {
            await wf.MarkImplementedAsync(pid);
            return Results.NoContent();
        });

        // ------------------------------------------------------------ import / export
        api.MapGet("/cycles/{id:int}/export/powerbi", async (int id, WorkloadService ws) =>
        {
            var s = await ws.SnapshotAsync(id);
            return Results.File(ExportService.PowerBiZip(s, WorkloadService.Projects(s)), "application/zip",
                                $"{s.Cycle.Name.Replace(' ', '_')}_powerbi.zip");
        }).RequireAuthorization(policy => policy.RequireRole(cp, Roles.Viewer, Roles.Admin));
        api.MapGet("/cycles/{id:int}/export/excel", async (int id, WorkloadService ws) =>
        {
            var s = await ws.SnapshotAsync(id);
            return Results.File(ExportService.ExcelReport(s, WorkloadService.Departments(s), WorkloadService.Projects(s), WorkloadService.DeviationRegister(s)),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{s.Cycle.Name.Replace(' ', '_')}_workload_report.xlsx");
        }).RequireAuthorization(policy => policy.RequireRole(cp, Roles.Viewer, Roles.Admin));
        api.MapGet("/cycles/{id:int}/templates/{kind}", async (int id, string kind, WorkloadDbContext db) =>
        {
            var c = await db.Cycles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            return c is null ? Results.NotFound()
                : Results.File(ImportService.Template(c, kind), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                               $"{c.Name.Replace(' ', '_')}_{kind}_template.xlsx");
        });
        api.MapPost("/cycles/{id:int}/import/{kind}", async (int id, string kind, IFormFile file, ImportService import) =>
        {
            using var stream = file.OpenReadStream();
            var sheet = ImportService.Read(stream, file.FileName);
            return kind switch
            {
                "planning" => Results.Ok(await import.ImportPlanningAsync(id, sheet)),
                "proposals" => Results.Ok(await import.ImportProposalsAsync(id, sheet)),
                "after" => Results.Ok(await import.ImportAfterAsync(id, sheet)),
                _ => Results.BadRequest(new { error = "kind must be planning, proposals or after" }),
            };
        }).RequireAuthorization(cp).DisableAntiforgery();

        // ------------------------------------------------------------ administration
        var admin = api.MapGroup("/admin").RequireAuthorization(policy => policy.RequireRole(Roles.Admin, cp));
        admin.MapPost("/import/{kind}", async (string kind, IFormFile file, ImportService import) =>
        {
            using var stream = file.OpenReadStream();
            var sheet = ImportService.Read(stream, file.FileName);
            return kind switch
            {
                "departments" => Results.Ok(await import.ImportDepartmentsAsync(sheet)),
                "projects" => Results.Ok(await import.ImportProjectsAsync(sheet)),
                _ => Results.BadRequest(new { error = "kind must be departments or projects" }),
            };
        }).DisableAntiforgery();
        admin.MapGet("/users", async (WorkloadDbContext db) => await db.Users.AsNoTracking().OrderBy(u => u.Role).ThenBy(u => u.Upn).ToListAsync());
        admin.MapPut("/users", async (UserInput input, WorkloadDbContext db, CurrentUser u) =>
        {
            if (!Roles.All.Contains(input.Role)) return Results.BadRequest(new { error = $"Role must be one of {string.Join(", ", Roles.All)}" });
            var upn = WorkflowService.Text(input.Upn, 120, "The sign-in name").ToLowerInvariant();
            if (upn.Length == 0) return Results.BadRequest(new { error = "Give the user's sign-in name (UPN)." });
            var acc = await db.Users.FindAsync(upn);
            if (acc is null) db.Users.Add(acc = new UserAccount { Upn = upn });
            (acc.DisplayName, acc.Role, acc.DeptCodes, acc.Email, acc.IsActive) = (WorkflowService.Text(input.DisplayName, 120, "The name"), input.Role,
                WorkflowService.Text(input.DeptCodes, 200, "The department list") is { Length: > 0 } dc ? dc : null,
                WorkflowService.Text(input.Email, 200, "The e-mail") is { Length: > 0 } em ? em : null, input.IsActive);
            db.Log(u.Upn, "SaveUser", "User", upn, null, $"{input.Role} {input.DeptCodes}");
            await db.SaveChangesAsync();
            return Results.Ok(acc);
        }).RequireAuthorization(policy => policy.RequireRole(Roles.Admin));
        admin.MapPut("/departments/{code}", async (string code, DepartmentInput input, WorkloadDbContext db, CurrentUser u) =>
        {
            if (code.Length > 8) return Results.BadRequest(new { error = "Department codes have at most 8 characters." });
            if (new[] { input.Permanent, input.Temporary, input.Managers, input.LoanToUnpaid }.Any(x => x is < 0 or > 100_000))
                return Results.BadRequest(new { error = "Headcounts must be between 0 and 100,000." });
            var d = await db.Departments.FindAsync(code);
            if (d is null) db.Departments.Add(d = new Department { Code = code });
            (d.Name, d.ParentCode, d.Permanent, d.Temporary, d.Managers, d.LoanToUnpaid, d.IsActive) =
                (WorkflowService.Text(input.Name, 120, "The department name"),
                 WorkflowService.Text(input.ParentCode, 8, "The parent code") is { Length: > 0 } pc ? pc : null,
                 input.Permanent, input.Temporary, input.Managers, input.LoanToUnpaid, input.IsActive);
            db.Log(u.Upn, "SaveDepartment", "Department", code, null, $"perm {input.Permanent}, temp {input.Temporary}");
            await db.SaveChangesAsync();
            return Results.Ok(d);
        });
        admin.MapPut("/settings", async (Dictionary<string, double> values, WorkloadDbContext db, CurrentUser u) =>
        {
            var known = typeof(WorkloadSettings).GetProperties().Select(p => p.Name).ToHashSet();
            if (values.Any(kv => known.Contains(kv.Key) && (!double.IsFinite(kv.Value) || kv.Value < 0 || kv.Value > 1_000_000)))
                return Results.BadRequest(new { error = "Settings must be non-negative numbers." });
            foreach (var (k, v) in values.Where(kv => known.Contains(kv.Key)))
            {
                var s = await db.Settings.FindAsync(k);
                if (s is null) db.Settings.Add(s = new AppSetting { Key = k });
                s.Value = v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            db.Log(u.Upn, "SaveSettings", "Settings", "*", null, string.Join(", ", values.Select(kv => $"{kv.Key}={kv.Value}")));
            await db.SaveChangesAsync();
            return Results.Ok(await db.LoadSettingsAsync());
        }).RequireAuthorization(policy => policy.RequireRole(Roles.Admin));
    }

    /// <summary>Development sign-in: lists the seeded users and sets the dev_user cookie. Mapped only when Auth:Mode is Development.</summary>
    public static void MapDevelopmentLogin(this WebApplication app)
    {
        app.MapGet("/api/dev/users", async (WorkloadDbContext db) =>
            await db.Users.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.Role).Select(u => new { u.Upn, u.DisplayName, u.Role, u.DeptCodes }).ToListAsync());
        app.MapPost("/api/dev/login", async ([FromBody] LoginInput input, WorkloadDbContext db, HttpContext http) =>
        {
            var upn = input.Upn.Trim().ToLowerInvariant();
            if (!await db.Users.AnyAsync(u => u.Upn == upn && u.IsActive)) return Results.BadRequest(new { error = "Unknown user" });
            http.Response.Cookies.Append(DevAuthHandler.Cookie, upn, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict });
            return Results.NoContent();
        });
        app.MapPost("/api/dev/logout", (HttpContext http) =>
        {
            http.Response.Cookies.Delete(DevAuthHandler.Cookie);
            return Results.NoContent();
        });
    }
}
