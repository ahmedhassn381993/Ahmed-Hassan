using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Enppi.Workload.Api.Auth;
using Enppi.Workload.Api.Data;
using Enppi.Workload.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Enppi.Workload.Api.Services;

public record ImportResult(int Created, int Updated, int Skipped, List<string> Warnings);

/// <summary>A sheet read as header names + rows of raw cell values (string, double or DateTime). <see cref="Others"/>
/// holds the workbook's other sheets by name (empty for CSV).</summary>
public record SheetData(List<string> Headers, List<object?[]> Rows)
{
    public Dictionary<string, SheetData> Others { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Imports the standard templates. Planning, proposals and after-implementation use one wide layout:
/// Dept | SubDept | Category | ProjectCode | Activity | Planner | Reason | one column per month (date or "Jul-26").
/// The comments analysis engine writes these files from the legacy department workbooks (export_webapp.py).</summary>
public class ImportService(WorkloadDbContext db, CurrentUser user)
{
    public static readonly string[] ForecastColumns = { "Dept", "SubDept", "Category", "ProjectCode", "Activity", "Planner", "Reason", "Occurrence", "RequestedCategory" };

    public static SheetData Read(Stream stream, string fileName)
    {
        if (fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return ReadCsv(stream);
        XLWorkbook wb;
        try { wb = new XLWorkbook(stream); }
        catch (Exception e) when (e is not RuleException) { throw new RuleException("The file is not a readable Excel workbook (.xlsx) or CSV."); }
        using (wb)
        {
            var first = ReadSheet(wb.Worksheets.First()) ?? throw new RuleException("The sheet is empty.");
            foreach (var ws in wb.Worksheets.Skip(1))
                if (ReadSheet(ws) is { } other) first.Others[ws.Name] = other;
            return first;
        }
    }

    private static SheetData? ReadSheet(IXLWorksheet ws)
    {
        var used = ws.RangeUsed();
        if (used is null) return null;
        int firstRow = used.FirstRow().RowNumber(), lastRow = used.LastRow().RowNumber(), lastCol = used.LastColumn().ColumnNumber();
        var headers = Enumerable.Range(1, lastCol).Select(c => CellText(ws.Cell(firstRow, c))).ToList();
        var rows = new List<object?[]>();
        for (int r = firstRow + 1; r <= lastRow; r++)
        {
            var row = Enumerable.Range(1, lastCol).Select(c => CellValue(ws.Cell(r, c))).ToArray();
            if (row.Any(v => v is not null && !(v is string s && s.Length == 0))) rows.Add(row);
        }
        return new SheetData(headers, rows);
    }

    /// <summary>Header text; month headers saved as Excel date serials (a custom "mmm-yy" format is not always recognised
    /// as a date) are turned back into dates.</summary>
    private static string CellText(IXLCell c) =>
        c.Value.IsDateTime ? c.Value.GetDateTime().ToString("yyyy-MM-dd")
        : c.Value.IsNumber && c.Value.GetNumber() is >= 20000 and <= 80000 ? DateTime.FromOADate(c.Value.GetNumber()).ToString("yyyy-MM-dd")
        : c.GetString().Trim();

    private static object? CellValue(IXLCell c)
    {
        var v = c.Value;
        if (v.IsBlank) return null;
        if (v.IsNumber) return v.GetNumber();
        if (v.IsDateTime) return v.GetDateTime();
        return v.ToString().Trim();
    }

    private static SheetData ReadCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<List<string>>();
        var text = reader.ReadToEnd();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                if (row.Any(f => f.Length > 0)) lines.Add(row);
                row = new List<string>();
            }
            else field.Append(ch);
        }
        row.Add(field.ToString());
        if (row.Any(f => f.Length > 0)) lines.Add(row);
        if (lines.Count == 0) throw new RuleException("The file is empty.");
        object? Parse(string s) => s.Length == 0 ? null
            : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : s.Trim();
        return new SheetData(lines[0].Select(h => h.Trim()).ToList(), lines.Skip(1).Select(r => r.Select(Parse).ToArray()).ToList());
    }

    private static readonly string[] MonthFormats = { "yyyy-MM-dd", "yyyy-MM", "MMM-yy", "MMM yy", "MMM-yyyy", "MMM yyyy", "MMMM yyyy", "M/d/yyyy", "d/M/yyyy" };

    /// <summary>Header -> month index in the cycle; null when the header is not a month.</summary>
    public static int? MonthIndex(string header, ForecastCycle c)
    {
        if (!DateTime.TryParseExact(header.Trim(), MonthFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) &&
            !DateTime.TryParse(header, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return null;
        int idx = (d.Year - c.FirstMonth.Year) * 12 + d.Month - c.FirstMonth.Month;
        return idx;
    }

    private static string S(object? v) => v switch
    {
        null => "",
        double d when Math.Abs(d - Math.Round(d)) < 1e-9 => ((long)d).ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        DateTime t => t.ToString("yyyy-MM-dd"),
        _ => v.ToString()!.Trim(),
    };

    private static double? N(object? v)
    {
        double? d = v switch
        {
            null => null,
            double x => x,
            string s when s.Length == 0 => null,
            string s when double.TryParse(s.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) => x,
            _ => throw new RuleException($"'{v}' is not a number"),
        };
        if (Rules.InvalidMH(d) is { } why) throw new RuleException($"'{v}': {why}");
        return d;
    }

    /// <summary>Occurrence: which of several rows with the same department / sub-department / category / code this is
    /// (1, 2...). Optional; without it rows are numbered in file order, which is only safe for complete files.</summary>
    private record ForecastRow(int RowNumber, string Dept, string Section, Category Category, string Code, string Activity,
                               string Planner, string Reason, Dictionary<int, double?> Months, int? Occurrence,
                               Category? RequestedCategory);

    private static List<ForecastRow> ParseForecast(SheetData sheet, ForecastCycle c, List<string> warnings)
    {
        int Col(string name) => sheet.Headers.FindIndex(h => string.Equals(h.Replace(" ", ""), name, StringComparison.OrdinalIgnoreCase));
        int dept = Col("Dept"), sec = Col("SubDept"), cat = Col("Category"), code = Col("ProjectCode"), act = Col("Activity"),
            plan = Col("Planner"), reason = Col("Reason"), occCol = Col("Occurrence"), reqCol = Col("RequestedCategory");
        if (dept < 0 || cat < 0 || code < 0) throw new RuleException("Columns Dept, Category and ProjectCode are required.");
        var monthCols = sheet.Headers.Select((h, i) => (i, idx: ForecastColumns.Contains(h, StringComparer.OrdinalIgnoreCase) ? null : MonthIndex(h, c)))
            .Where(x => x.idx != null).ToList();
        if (monthCols.Count == 0) throw new RuleException("No month columns found (use dates or headers like Jul-26).");
        int outside = monthCols.Count(x => x.idx < 0 || x.idx >= c.MonthCount);
        if (outside > 0) warnings.Add($"{outside} month columns fall outside {c.Name} ({c.FirstMonth:MMM-yy} + {c.MonthCount} months) and were ignored.");
        var rows = new List<ForecastRow>();
        for (int r = 0; r < sheet.Rows.Count; r++)
        {
            var x = sheet.Rows[r];
            object? At(int i) => i >= 0 && i < x.Length ? x[i] : null;
            var codeText = S(At(code));
            if (codeText.Length == 0) { warnings.Add($"Row {r + 2}: no project code - skipped."); continue; }
            Category category;
            try { category = Categories.Parse(S(At(cat))); }
            catch (FormatException e) { warnings.Add($"Row {r + 2}: {e.Message} - skipped."); continue; }
            var months = new Dictionary<int, double?>();
            foreach (var (i, idx) in monthCols.Where(m => m.idx >= 0 && m.idx < c.MonthCount))
            {
                try { months[idx!.Value] = N(At(i)); }
                catch (RuleException e) { warnings.Add($"Row {r + 2}, {sheet.Headers[i]}: {e.Message} - left blank."); months[idx!.Value] = null; }
            }
            var d = S(At(dept));
            var occ = N(At(occCol)) is double o && o >= 1 ? (int?)(int)o : null;
            Category? requested = null;
            if (S(At(reqCol)) is { Length: > 0 } rc)
            {
                try { requested = Categories.Parse(rc); }
                catch (FormatException e) { warnings.Add($"Row {r + 2}: {e.Message} - category request ignored."); }
            }
            rows.Add(new ForecastRow(r + 2, d, S(At(sec)) is { Length: > 0 } s ? s : d, category, codeText, S(At(act)),
                                     S(At(plan)), S(At(reason)), months, occ, requested));
        }
        return rows;
    }

    private static string Key(string dept, string sec, Category cat, string code, int occ) => $"{dept}|{sec}|{cat}|{code}|{occ}";

    private static Dictionary<string, ForecastLine> LineIndex(IEnumerable<ForecastLine> lines)
    {
        var index = new Dictionary<string, ForecastLine>();
        foreach (var g in lines.OrderBy(l => l.Id).GroupBy(l => (l.DeptCode, l.SectionCode, l.Category, l.ProjectCode)))
        {
            int occ = 0;
            foreach (var l in g) index[Key(l.DeptCode, l.SectionCode, l.Category, l.ProjectCode, ++occ)] = l;
        }
        return index;
    }

    private async Task<ForecastCycle> OpenCycleAsync(int cycleId)
    {
        if (!user.IsCorporatePlanning) throw new ForbiddenException("Only Corporate Planning imports data.");
        var c = await db.Cycles.FirstOrDefaultAsync(x => x.Id == cycleId) ?? throw new KeyNotFoundException($"Cycle {cycleId} not found");
        if (c.Status == CycleStatus.Locked) throw new RuleException($"Cycle {c.Name} is locked.");
        return c;
    }

    private async Task EnsureProjectsAsync(IEnumerable<ForecastRow> rows, List<string> warnings, bool requestsOnly = false)
    {
        var all = await db.Projects.AsNoTracking().ToDictionaryAsync(p => p.Code);
        foreach (var r in rows.Where(r => !all.ContainsKey(r.Code)).GroupBy(r => r.Code))
        {
            // a phase of a master project (5248200-1 of 5248200) is in the master, as in the comments analysis
            var master = all.GetValueOrDefault(Rules.BaseCode(r.Key)) is { InMaster: true } m && m.Code != r.Key ? m : null;
            if (requestsOnly)
            {
                // new allocations: the project row is created when Corporate Planning approves the request
                if (master is null) warnings.Add($"Project {r.Key} is not in the project master; it is recorded on the request until approved.");
                continue;
            }
            var p = ProjectCatalog.FromBase(r.Key, r.First().Activity, master);
            db.Projects.Add(p);
            all[r.Key] = p;
            if (!p.InMaster) warnings.Add($"Project {r.Key} is not in the project master; added as an unconfirmed code.");
        }
        var depts = (await db.Departments.Select(d => d.Code).ToListAsync()).ToHashSet();
        foreach (var d in rows.Select(r => r.Dept).Distinct().Where(d => !depts.Contains(d)))
            warnings.Add($"Department {d} is not in the department master; add it under Admin so its capacity is known.");
    }

    /// <summary>Planning Input (the "To Departments" values): creates lines or replaces planner values.</summary>
    public async Task<ImportResult> ImportPlanningAsync(int cycleId, SheetData sheet)
    {
        var c = await OpenCycleAsync(cycleId);
        var warnings = new List<string>();
        var rows = ParseForecast(sheet, c, warnings);
        await EnsureProjectsAsync(rows, warnings);
        var index = LineIndex(await db.Lines.Include(l => l.Values).Where(l => l.CycleId == cycleId).ToListAsync());
        var occ = new Dictionary<string, int>();
        int created = 0, updated = 0;
        foreach (var r in rows)
        {
            var baseKey = $"{r.Dept}|{r.Section}|{r.Category}|{r.Code}";
            occ[baseKey] = r.Occurrence ?? occ.GetValueOrDefault(baseKey) + 1;
            if (!index.TryGetValue(Key(r.Dept, r.Section, r.Category, r.Code, occ[baseKey]), out var line))
            {
                line = new ForecastLine { CycleId = cycleId, DeptCode = r.Dept, SectionCode = r.Section, Category = r.Category,
                                          ProjectCode = r.Code, Activity = r.Activity, Origin = LineOrigin.Import };
                db.Lines.Add(line);
                created++;
            }
            else
            {
                updated++;
                if (line.Origin == LineOrigin.AfterExtract) line.Origin = LineOrigin.Import;
            }
            if (r.Planner.Length > 0) line.PlannerUpn = r.Planner.ToLowerInvariant();
            if (r.Activity.Length > 0) line.Activity = r.Activity;
            foreach (var (m, val) in r.Months)
            {
                var v = line.Values.FirstOrDefault(x => x.MonthIndex == m);
                if (v is null) line.Values.Add(v = new ForecastValue { MonthIndex = m });
                v.PlannerMH = val;
            }
        }
        db.Log(user.Upn, "ImportPlanning", "Cycle", cycleId.ToString(), cycleId, $"{created} created, {updated} updated");
        await db.SaveChangesAsync();
        return new ImportResult(created, updated, 0, warnings);
    }

    /// <summary>After-implementation extract (Primavera re-extract after planners implemented): fills AfterMH;
    /// rows without a planning line are added (changes made by Planning without a comment).</summary>
    public async Task<ImportResult> ImportAfterAsync(int cycleId, SheetData sheet)
    {
        var c = await OpenCycleAsync(cycleId);
        var warnings = new List<string>();
        var rows = ParseForecast(sheet, c, warnings);
        await EnsureProjectsAsync(rows, warnings);
        var lines = await db.Lines.Include(l => l.Values).Where(l => l.CycleId == cycleId).ToListAsync();
        foreach (var v in lines.SelectMany(l => l.Values)) v.AfterMH = null;
        foreach (var l in lines) l.AfterCategory = null;
        var proposals = await db.Proposals.Where(p => p.CycleId == cycleId && p.Status != ProposalStatus.Draft).ToListAsync();
        var index = LineIndex(lines);
        var occ = new Dictionary<string, int>();
        var matched = new HashSet<ForecastLine>();
        int created = 0, updated = 0, moved = 0, requested = 0;
        foreach (var r in rows)
        {
            var baseKey = $"{r.Dept}|{r.Section}|{r.Category}|{r.Code}";
            occ[baseKey] = r.Occurrence ?? occ.GetValueOrDefault(baseKey) + 1;
            if (!index.TryGetValue(Key(r.Dept, r.Section, r.Category, r.Code, occ[baseKey]), out var line) || matched.Contains(line))
            {
                line = null;
                // the planner filed the row under the category the department asked for (approved or still pending)
                var movedTo = proposals.Where(p => p.Kind == ProposalKind.CategoryChange && p.RequestedCategory == r.Category &&
                                                   p.Status != ProposalStatus.Rejected).Select(p => p.LineId).ToHashSet();
                line = lines.FirstOrDefault(l => !matched.Contains(l) && l.DeptCode == r.Dept && l.SectionCode == r.Section && l.ProjectCode == r.Code &&
                                                 l.Category != r.Category && (l.ApprovedCategory == r.Category || movedTo.Contains(l.Id)));
                if (line is not null) { line.AfterCategory = r.Category; moved++; }
            }
            if (line is null &&
                proposals.FirstOrDefault(p => p.Kind == ProposalKind.NewAllocation && p.LineId == null && p.Line == null && p.DeptCode == r.Dept &&
                                              (p.NewSectionCode ?? p.DeptCode) == r.Section && (p.NewCategory ?? Category.Proposal) == r.Category &&
                                              p.NewProjectCode == r.Code) is { } request)
            {
                // a new allocation the planner already carried into Primavera: the request and the after values share one line
                line = new ForecastLine { CycleId = cycleId, DeptCode = r.Dept, SectionCode = r.Section, Category = r.Category,
                                          ProjectCode = r.Code, Activity = request.NewActivity ?? r.Activity, Origin = LineOrigin.DepartmentRequest };
                db.Lines.Add(line);
                lines.Add(line);
                request.Line = line;
                requested++;
            }
            if (line is null)
            {
                line = new ForecastLine { CycleId = cycleId, DeptCode = r.Dept, SectionCode = r.Section, Category = r.Category,
                                          ProjectCode = r.Code, Activity = r.Activity, Origin = LineOrigin.AfterExtract };
                db.Lines.Add(line);
                lines.Add(line);
                created++;
            }
            else updated++;
            matched.Add(line);
            foreach (var (m, val) in r.Months)
            {
                var v = line.Values.FirstOrDefault(x => x.MonthIndex == m);
                if (v is null) line.Values.Add(v = new ForecastValue { MonthIndex = m });
                v.AfterMH = val ?? 0;
            }
        }
        c.HasAfterData = true;
        if (created > 0) warnings.Add($"{created} rows exist only in the after extract (added by Planning without a comment).");
        if (moved > 0) warnings.Add($"{moved} rows are filed under the category their department asked for; matched to the original line.");
        if (requested > 0) warnings.Add($"{requested} rows carry new allocations that are still requests; matched to those requests.");
        db.Log(user.Upn, "ImportAfter", "Cycle", cycleId.ToString(), cycleId, $"{updated} matched, {created} after-only");
        await db.SaveChangesAsync();
        return new ImportResult(created, updated, 0, warnings);
    }

    /// <summary>Migration of department feedback files (-c workbooks): blank = no change; a row without a planning line
    /// becomes a new-allocation request. Imported proposals are submitted on the department's behalf. Importing the same
    /// file again updates the pending requests; requests Corporate Planning already decided are left as they are.
    /// An optional sheet "Reviewed departments" (column Dept) lists every department whose feedback was processed, so
    /// departments that answered without a man-hour change are recorded as "no change".</summary>
    public async Task<ImportResult> ImportProposalsAsync(int cycleId, SheetData sheet)
    {
        var c = await OpenCycleAsync(cycleId);
        var warnings = new List<string>();
        var rows = ParseForecast(sheet, c, warnings);
        var lines = await db.Lines.Include(l => l.Values).Where(l => l.CycleId == cycleId).ToListAsync();
        var index = LineIndex(lines);
        await EnsureProjectsAsync(rows.Where(r => !index.ContainsKey(Key(r.Dept, r.Section, r.Category, r.Code, 1))), warnings, requestsOnly: true);
        var existing = await db.Proposals.Include(p => p.Values).Where(p => p.CycleId == cycleId).ToListAsync();
        var baseline = lines.Where(WorkloadService.InBaseline).ToList();
        var occ = new Dictionary<string, int>();
        var newOcc = new Dictionary<string, int>();
        int created = 0, updated = 0, skipped = 0, decided = 0;
        var depts = new HashSet<string>();
        bool Decided(Proposal p) => p.Status is ProposalStatus.Approved or ProposalStatus.PartiallyApproved or ProposalStatus.Rejected;
        void Submit(Proposal p, string reason)
        {
            p.Reason = reason;
            p.Status = ProposalStatus.Submitted;
            p.SubmittedAt = DateTime.UtcNow;
        }
        foreach (var r in rows)
        {
            depts.Add(r.Dept);
            var baseKey = $"{r.Dept}|{r.Section}|{r.Category}|{r.Code}";
            occ[baseKey] = r.Occurrence ?? occ.GetValueOrDefault(baseKey) + 1;
            var reason = WorkflowService.Text(r.Reason.Length > 0 ? r.Reason : "Imported from the department's feedback workbook", 2000, $"Row {r.RowNumber}: the reason");
            if (index.TryGetValue(Key(r.Dept, r.Section, r.Category, r.Code, occ[baseKey]), out var line) && WorkloadService.InBaseline(line))
            {
                var planner = Enumerable.Range(0, c.MonthCount)
                    .Select(m => line.Values.FirstOrDefault(v => v.MonthIndex == m)?.PlannerMH ?? 0).ToArray();
                var proposed = Enumerable.Range(0, c.MonthCount).Select(m => r.Months.GetValueOrDefault(m)).ToArray();
                var change = Rules.Classify(planner, proposed);
                if (r.RequestedCategory is { } rc && rc != line.Category)
                {
                    // the department filed the project under another category: a category-change request
                    var cr = existing.FirstOrDefault(x => x.LineId == line.Id && x.Kind == ProposalKind.CategoryChange);
                    if (cr is not null && Decided(cr)) decided++;
                    else
                    {
                        if (cr is null) { cr = new Proposal { CycleId = cycleId, LineId = line.Id, DeptCode = r.Dept, Kind = ProposalKind.CategoryChange, CreatedBy = user.Upn }; db.Proposals.Add(cr); existing.Add(cr); created++; }
                        cr.RequestedCategory = rc;
                        cr.ChangeTypes = "Phase Transfer";
                        Submit(cr, reason);
                    }
                }
                if (change.MonthsChanged.Count == 0) { if (r.RequestedCategory is null) skipped++; continue; }
                var p = existing.FirstOrDefault(x => x.LineId == line.Id && x.Kind == ProposalKind.Change);
                if (p is not null && Decided(p)) { decided++; continue; }
                if (p is null) { p = new Proposal { CycleId = cycleId, LineId = line.Id, DeptCode = r.Dept, Kind = ProposalKind.Change, CreatedBy = user.Upn }; db.Proposals.Add(p); existing.Add(p); created++; }
                else updated++;
                p.Values.Clear();
                foreach (var (m, val) in r.Months.Where(kv => kv.Value != null))
                    p.Values.Add(new ProposalValue { MonthIndex = m, ProposedMH = val });
                p.ChangeTypes = change.TypesText;
                Submit(p, reason);
            }
            else
            {
                if (r.Months.Values.All(v => (v ?? 0) < Rules.Tol)) { skipped++; continue; }
                // the same new allocation imported again updates its request (matched by department, sub-department,
                // category, code and occurrence) instead of adding it twice
                newOcc[baseKey] = r.Occurrence ?? newOcc.GetValueOrDefault(baseKey) + 1;
                var same = existing.Where(x => x.Kind == ProposalKind.NewAllocation && x.DeptCode == r.Dept &&
                                               (x.NewSectionCode ?? x.DeptCode) == r.Section && x.NewCategory == r.Category && x.NewProjectCode == r.Code)
                    .OrderBy(x => x.Id).ToList();
                var p = same.Count >= newOcc[baseKey] ? same[newOcc[baseKey] - 1] : null;
                if (p is not null && Decided(p)) { decided++; continue; }
                if (p is null)
                {
                    p = new Proposal { CycleId = cycleId, DeptCode = r.Dept, Kind = ProposalKind.NewAllocation, NewProjectCode = r.Code,
                                       NewCategory = r.Category, NewSectionCode = r.Section, CreatedBy = user.Upn };
                    db.Proposals.Add(p);
                    existing.Add(p);
                    created++;
                }
                else updated++;
                p.NewActivity = WorkflowService.Text(r.Activity, 250, $"Row {r.RowNumber}: the activity");
                p.Values.Clear();
                foreach (var (m, val) in r.Months.Where(kv => kv.Value != null)) p.Values.Add(new ProposalValue { MonthIndex = m, ProposedMH = val });
                p.ChangeTypes = Rules.NewAllocationType(baseline.Any(l => l.DeptCode == r.Dept && Rules.BaseCode(l.ProjectCode) == Rules.BaseCode(r.Code)),
                                                        baseline.Any(l => l.DeptCode == r.Dept && l.SectionCode == r.Section && l.Category == r.Category && l.ProjectCode == r.Code));
                Submit(p, reason);
            }
        }
        if (decided > 0) warnings.Add($"{decided} rows belong to requests Corporate Planning already decided; they were left unchanged (return them to change).");
        // departments that answered without a man-hour change
        var reviewed = new HashSet<string>(depts);
        if (sheet.Others.TryGetValue("Reviewed departments", out var list))
        {
            int col = list.Headers.FindIndex(h => string.Equals(h.Trim(), "Dept", StringComparison.OrdinalIgnoreCase));
            if (col < 0) warnings.Add("Sheet 'Reviewed departments' has no Dept column - ignored.");
            else foreach (var row in list.Rows) if (S(col < row.Length ? row[col] : null) is { Length: > 0 } d) reviewed.Add(d);
        }
        foreach (var d in reviewed)
        {
            var rev = await db.DepartmentReviews.FirstOrDefaultAsync(x => x.CycleId == cycleId && x.DeptCode == d)
                      ?? db.DepartmentReviews.Add(new DepartmentReview { CycleId = cycleId, DeptCode = d }).Entity;
            bool any = depts.Contains(d) || existing.Any(p => p.DeptCode == d && p.Status != ProposalStatus.Draft);
            if (!any && rev.Status == DeptReviewStatus.Submitted) continue;
            rev.Status = any ? DeptReviewStatus.Submitted : DeptReviewStatus.NoChangeConfirmed;
            rev.SubmittedAt = DateTime.UtcNow;
            rev.SubmittedBy = user.Upn + " (import)";
        }
        db.Log(user.Upn, "ImportProposals", "Cycle", cycleId.ToString(), cycleId,
               $"{created} created, {updated} updated, {skipped} unchanged rows, {decided} decided rows kept");
        await db.SaveChangesAsync();
        return new ImportResult(created, updated, skipped + decided, warnings);
    }

    public async Task<ImportResult> ImportDepartmentsAsync(SheetData sheet)
    {
        if (!user.Is(Roles.Admin) && !user.IsCorporatePlanning) throw new ForbiddenException("Only administrators maintain reference data.");
        int Col(string n) => sheet.Headers.FindIndex(h => string.Equals(h.Replace(" ", ""), n, StringComparison.OrdinalIgnoreCase));
        int code = Col("Code"), name = Col("Name"), parent = Col("ParentCode"), perm = Col("Permanent"), temp = Col("Temporary"),
            mgr = Col("Managers"), loan = Col("LoanToUnpaid");
        if (code < 0 || name < 0) throw new RuleException("Columns Code and Name are required.");
        int created = 0, updated = 0;
        foreach (var r in sheet.Rows)
        {
            object? At(int i) => i >= 0 && i < r.Length ? r[i] : null;
            var c = S(At(code));
            if (c.Length == 0) continue;
            var d = await db.Departments.FindAsync(c);
            if (d is null) { db.Departments.Add(d = new Department { Code = c }); created++; } else updated++;
            if (c.Length > 8) throw new RuleException($"Department code '{c}' is longer than 8 characters.");
            d.Name = WorkflowService.Text(S(At(name)), 120, $"Department {c}: the name");
            if (parent >= 0) d.ParentCode = S(At(parent)) is { Length: > 0 } p ? WorkflowService.Text(p, 8, $"Department {c}: the parent code") : null;
            int Heads(int col, int current) => N(At(col)) is { } x ? (x <= 100_000 ? (int)x : throw new RuleException($"Department {c}: headcount {x} is too large.")) : current;
            d.Permanent = Heads(perm, d.Permanent);
            d.Temporary = Heads(temp, d.Temporary);
            d.Managers = Heads(mgr, d.Managers);
            d.LoanToUnpaid = Heads(loan, d.LoanToUnpaid);
        }
        db.Log(user.Upn, "ImportDepartments", "Department", "*", null, $"{created} created, {updated} updated");
        await db.SaveChangesAsync();
        return new ImportResult(created, updated, 0, new());
    }

    public async Task<ImportResult> ImportProjectsAsync(SheetData sheet)
    {
        if (!user.Is(Roles.Admin) && !user.IsCorporatePlanning) throw new ForbiddenException("Only administrators maintain reference data.");
        int Col(string n) => sheet.Headers.FindIndex(h => string.Equals(h.Replace(" ", "").Replace("_", ""), n, StringComparison.OrdinalIgnoreCase));
        int code = Col("Code") is >= 0 and var c1 ? c1 : Col("ProjectNo"), name = Col("Name") is >= 0 and var n1 ? n1 : Col("ProjectName"),
            client = Col("Client") is >= 0 and var k1 ? k1 : Col("ClientShortName"), cat = Col("Category") is >= 0 and var t1 ? t1 : Col("ProjectCategory");
        if (code < 0 || name < 0) throw new RuleException("Columns Code and Name are required.");
        var all = await db.Projects.ToDictionaryAsync(p => p.Code);
        int created = 0, updated = 0;
        foreach (var r in sheet.Rows)
        {
            object? At(int i) => i >= 0 && i < r.Length ? r[i] : null;
            var c = S(At(code));
            if (c.Length == 0) continue;
            if (c.Length > 20) throw new RuleException($"Project code '{c}' is longer than 20 characters.");
            if (!all.TryGetValue(c, out var p)) { db.Projects.Add(p = new Project { Code = c }); all[c] = p; created++; } else updated++;
            p.BaseCode = Rules.BaseCode(c);
            p.Name = WorkflowService.Text(S(At(name)), 250, $"Project {c}: the name");
            p.Client = S(At(client)) is { Length: > 0 } cl ? WorkflowService.Text(cl, 60, $"Project {c}: the client") : p.Client;
            p.MasterCategory = S(At(cat)) is { Length: > 0 } mc ? mc[..Math.Min(4, mc.Length)] : p.MasterCategory;
            p.InMaster = true;
        }
        // phases of master projects added earlier as unconfirmed codes (5248200-1) now belong to the master
        foreach (var p in all.Values.Where(p => !p.InMaster && p.BaseCode != p.Code))
            if (all.GetValueOrDefault(p.BaseCode) is { InMaster: true } m) (p.InMaster, p.Name, p.Client) = (true, m.Name, m.Client);
        db.Log(user.Upn, "ImportProjects", "Project", "*", null, $"{created} created, {updated} updated");
        await db.SaveChangesAsync();
        return new ImportResult(created, updated, 0, new());
    }

    /// <summary>Blank template with the cycle's month headers.</summary>
    public static byte[] Template(ForecastCycle c, string kind)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Forecast");
        var headers = ForecastColumns.ToList();
        for (int i = 0; i < headers.Count; i++) ws.Cell(1, i + 1).Value = headers[i];
        for (int m = 0; m < c.MonthCount; m++)
        {
            var cell = ws.Cell(1, headers.Count + m + 1);
            cell.Value = c.Month(m).ToDateTime(TimeOnly.MinValue);
            cell.Style.DateFormat.Format = "mmm-yy";
        }
        ws.Row(1).Style.Font.Bold = true;
        ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#1F3864");
        ws.Row(1).Style.Font.FontColor = XLColor.White;
        var notes = wb.AddWorksheet("How to fill");
        notes.Cell(1, 1).Value = kind switch
        {
            "planning" => "Planning Input: one row per department / sub-department / category / project. Month cells hold man-hours.",
            "proposals" => "Department proposals: leave a month blank to keep the planner value; type 0 only to remove the work. Reason is required.",
            _ => "After implementation: the Primavera re-extract after planners implemented the approved changes.",
        };
        notes.Cell(2, 1).Value = "Category: Running, Internal / Unforeseen, Loan To, Under Signature, Proposal, Potential Opportunity.";
        notes.Cell(3, 1).Value = "ProjectCode: the full code, e.g. 5248200-1 or 40061420 (never truncated).";
        ws.SheetView.FreezeRows(1);
        ws.Columns(1, 7).AdjustToContents();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
