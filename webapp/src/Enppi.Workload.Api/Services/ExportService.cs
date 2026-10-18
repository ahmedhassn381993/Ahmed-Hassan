using System.Globalization;
using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using Enppi.Workload.Api.Domain;

namespace Enppi.Workload.Api.Services;

/// <summary>Exports a cycle in the comments analysis engine's Power BI star schema (same file and column names), so the
/// Workload_Comments_Analysis.pbit template reads web-application data and engine output alike; plus an Excel report.</summary>
public static class ExportService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const string PlanningInput = "Planning Input", DisciplineComment = "Discipline Comment", AfterImplementation = "After Implementation";

    private sealed class Csv
    {
        private readonly StringBuilder _sb = new();
        public Csv(params string[] headers) => Row(headers.Cast<object?>().ToArray());
        public Csv Row(params object?[] values)
        {
            _sb.AppendJoin(',', values.Select(Format)).Append("\r\n");
            return this;
        }
        private static string Format(object? v)
        {
            var s = v switch
            {
                null => "",
                double d => Math.Round(d, 4).ToString("0.####", Inv),
                int i => i.ToString(Inv),
                DateOnly d => d.ToString("yyyy-MM-dd"),
                DateTime t => t.ToString("yyyy-MM-dd"),
                bool b => b ? "Yes" : "No",
                _ => v.ToString() ?? "",
            };
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        public byte[] Bytes() => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(_sb.ToString())).ToArray();
    }

    private static string RowKey(ScenarioRow r) =>
        $"{r.DeptCode}|{r.SectionCode}|{Categories.Label(r.Category)}|{(r.LineId is int id ? $"L{id}" : $"P{r.ProposalId}")}";

    public static byte[] PowerBiZip(CycleSnapshot s, IEnumerable<ProjectSeverity> projects)
    {
        var cycle = s.Cycle.Name;
        var months = s.Months;
        var files = new Dictionary<string, Csv>();

        var fact = new Csv("Cycle", "Scenario", "DeptCode", "Category", "ProjectCode", "RowKey", "Month", "MH");
        foreach (var r in s.Rows)
            for (int m = 0; m < months.Length; m++)
                foreach (var (scen, val) in new[] { (PlanningInput, r.Planning[m]), (DisciplineComment, r.Discipline[m]), (AfterImplementation, r.After[m]) })
                    if (Math.Abs(val) > 0) fact.Row(cycle, scen, r.DeptCode, Categories.Label(r.Category), r.ProjectCode, RowKey(r), months[m], val);
        files["FactWorkload.csv"] = fact;

        var items = new Csv("Cycle", "RowKey", "CycleRowKey", "DeptCode", "SubDept", "Category", "ProjectCode", "Activity",
            "Item Planning MH", "Item Discipline MH", "Item Implemented MH", "Gross Increase", "Gross Reduction", "Net Change", "Concern",
            "Primary Type", "Concern Types", "Implementation Status", "Item Implementation Rate", "Implemented Request MH",
            "Unrequested Change MH", "Requested Phase", "Code Correction", "Months Changed", "Blank Not Applied MH", "Feedback File",
            "Feedback Row", "Brief Categories", "Department Remark", "Blank Retained MH", "Bridge New Work MH", "Bridge Increase MH",
            "Bridge Extension MH", "Bridge Reduction MH", "Bridge Removed MH", "Master Flag");
        var bridge = new Csv("Cycle", "RowKey", "CycleRowKey", "ConcernType", "Brief Category");
        var cells = new Csv("Cycle", "RowKey", "CycleRowKey", "DeptCode", "ProjectCode", "Month", "Cell Planning MH", "Discipline Value",
            "Cell Discipline MH", "Change MH", "Cell Implemented MH", "Cell Status", "Blank Note");
        foreach (var r in s.Rows)
        {
            var key = RowKey(r);
            var c = r.Change;
            bool isNew = r.Proposal?.Kind == ProposalKind.NewAllocation, removed = c.Types.Contains("Removed Allocation");
            double share = WorkloadService.ImplementedShare(r);
            double unrequested = Enumerable.Range(0, months.Length).Where(m => !c.MonthsChanged.Contains(m))
                .Sum(m => Math.Abs(r.After[m] - r.Final[m]));
            s.Projects.TryGetValue(r.ProjectCode, out var master);
            items.Row(cycle, key, $"{cycle}|{key}", r.DeptCode, r.SectionCode, Categories.Label(r.Category), r.ProjectCode, r.Activity,
                r.Planning.Sum(), r.Discipline.Sum(), r.After.Sum(), c.GrossIncrease, c.GrossReduction, c.Net, r.Concern,
                c.Types.FirstOrDefault() ?? "No Change", c.TypesText, WorkloadService.ImplementationStatus(r, s.Cycle.HasAfterData),
                r.Concern ? share : null, r.Concern ? share * c.GrossChange : 0, unrequested,
                r.Proposal?.RequestedCategory is { } rc ? Categories.Label(rc) : "", "",
                string.Join(", ", c.MonthsChanged.Select(m => months[m].ToString("MMM-yy"))), 0, "web application", null,
                c.BriefCategories, r.Proposal?.Reason ?? "", 0,
                isNew ? c.Net : 0, isNew || removed ? 0 : c.GrossIncrease - c.ExtensionMH - c.EarlyStartMH,
                isNew || removed ? 0 : c.ExtensionMH + c.EarlyStartMH, isNew || removed ? 0 : c.GrossReduction, removed ? c.Net : 0,
                master is null ? "" : master.InMaster ? "" : "Not in project master (new or proposal code)");
            foreach (var t in c.Types) bridge.Row(cycle, key, $"{cycle}|{key}", t, Rules.BriefCategory(t));
            foreach (var m in c.MonthsChanged)
                cells.Row(cycle, key, $"{cycle}|{key}", r.DeptCode, r.ProjectCode, months[m], r.Planning[m],
                    r.Proposal?.Values.FirstOrDefault(v => v.MonthIndex == m)?.ProposedMH, r.Discipline[m],
                    r.Discipline[m] - r.Planning[m], r.After[m], WorkloadService.ImplementationStatus(r, s.Cycle.HasAfterData), "");
        }
        files["FactCommentItems.csv"] = items;
        files["BridgeConcernType.csv"] = bridge;
        files["FactCellChanges.csv"] = cells;

        var depts = new Csv("DeptCode", "Department", "Division", "Service", "Dept Label", "Managers", "LoanTo_Unpaid", "Permanent",
            "Temporary", "Total", "Effective_Heads", "Capacity_MH_per_Month", "Feedback Status", "Feedback Processed", "Sent To Dept",
            "Comments Received", "As Of");
        foreach (var d in s.Departments.Values.OrderBy(d => d.Code))
        {
            s.Reviews.TryGetValue(d.Code, out var rev);
            var status = rev?.Status ?? DeptReviewStatus.NotStarted;
            depts.Row(d.Code, d.Name, d.ParentCode ?? d.Code, "", $"{d.Code} - {d.Name}", d.Managers, d.LoanToUnpaid, d.Permanent,
                d.Temporary, d.Managers + d.LoanToUnpaid + d.Permanent + d.Temporary, Rules.EffectiveHeads(d, s.Settings),
                Rules.CapacityPerMonth(d, s.Settings), status.ToString(),
                status is DeptReviewStatus.Submitted or DeptReviewStatus.NoChangeConfirmed, null, rev?.SubmittedAt, DateTime.UtcNow);
        }
        files["DimDepartment.csv"] = depts;

        var proj = new Csv("ProjectCode", "Project Name", "Base Code", "Client", "Master Name", "Master Client", "Master Category",
            "Master Flag", "Title Variants", "Project Label", "Severity", "Severity Points", "Depts Concerned", "Departments Raising", "Severity Order");
        var order = new Dictionary<string, int> { ["Critical"] = 1, ["High"] = 2, ["Medium"] = 3, ["Low"] = 4, ["No Concern"] = 5 };
        foreach (var p in projects)
        {
            s.Projects.TryGetValue(p.ProjectCode, out var m);
            proj.Row(p.ProjectCode, p.Name, Rules.BaseCode(p.ProjectCode), p.Client ?? "", m?.Name ?? "", m?.Client ?? "", m?.MasterCategory ?? "",
                m is null || !m.InMaster ? "Not in project master (new or proposal code)" : "", "", $"{p.ProjectCode}  {p.Name}",
                p.Level, p.Points, p.DeptsRaising, p.Departments, order[p.Level]);
        }
        files["DimProject.csv"] = proj;

        var cat = new Csv("Category", "Category Order", "Parent Category", "Firm Workload", "Color");
        var colors = new[] { "#2E7D32", "#00897B", "#607D8B", "#6A1B9A", "#EF6C00", "#F9A825" };
        foreach (var (c, i) in Categories.Order.Select((c, i) => (c, i)))
            cat.Row(Categories.Label(c), i + 1, c == Category.Internal ? "Running" : Categories.Label(c), Categories.IsFirm(c), colors[i]);
        files["DimCategory.csv"] = cat;
        files["DimScenario.csv"] = new Csv("Scenario", "Scenario Order", "Color", "Meaning")
            .Row(PlanningInput, 1, "#2A78D6", "Planner values sent to departments")
            .Row(DisciplineComment, 2, "#EB6834", "Planner values with each department proposal applied (blank = retained)")
            .Row(AfterImplementation, 3, "#1BAF7A", s.Cycle.HasAfterData ? "Re-extracted forecast after planners implemented" : "Approved (final) values");
        var date = new Csv("Month", "Year", "Quarter", "Month Label", "Month Index", "Period");
        for (int i = 0; i < months.Length; i++)
            date.Row(months[i], months[i].Year, $"Q{(months[i].Month - 1) / 3 + 1}-{months[i].Year}", months[i].ToString("MMM-yy"), i + 1,
                     $"Months {6 * (i / 6) + 1}-{6 * (i / 6) + 6}");
        files["DimDate.csv"] = date;
        files["DataQuality.csv"] = new Csv("Cycle", "Severity", "Area", "Scenario", "File", "Issue", "Detail", "Impact_MH");
        var inv = new Csv("Dept", "Department", "Scenario", "File Used", "Source", "Sent to Dept", "Comments Received", "Action Needed", "Cycle");
        foreach (var d in s.Departments.Values.OrderBy(d => d.Code))
        {
            s.Reviews.TryGetValue(d.Code, out var rev);
            inv.Row(d.Code, d.Name, DisciplineComment, "web application", "Discipline Review screen", null, rev?.SubmittedAt,
                    rev?.Status is DeptReviewStatus.Submitted or DeptReviewStatus.NoChangeConfirmed ? "" : $"Review {rev?.Status ?? DeptReviewStatus.NotStarted}", cycle);
        }
        files["FileInventory.csv"] = inv;
        files["PossibleTransfers.csv"] = new Csv("Dept", "Reduced Project", "Reduced Phase", "Reduced Title", "Reduction MH", "Added Project",
            "Added Phase", "Added Title", "Added MH", "Match Basis", "Possible Offset MH", "Net of the Pair MH", "Cycle");

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var folder = cycle.Replace(' ', '_');
            foreach (var (name, csv) in files)
            {
                var entry = zip.CreateEntry($"{folder}/powerbi/{name}", CompressionLevel.Optimal);
                using var es = entry.Open();
                es.Write(csv.Bytes());
            }
        }
        return ms.ToArray();
    }

    public static byte[] ExcelReport(CycleSnapshot s, List<DepartmentSummary> depts, List<ProjectSeverity> projects, IEnumerable<DeviationItem> register)
    {
        using var wb = new XLWorkbook();
        var navy = XLColor.FromHtml("#1F3864");
        void Header(IXLWorksheet ws, int row, params string[] h)
        {
            for (int i = 0; i < h.Length; i++) ws.Cell(row, i + 1).Value = h[i];
            var r = ws.Range(row, 1, row, h.Length);
            r.Style.Font.Bold = true; r.Style.Font.FontColor = XLColor.White; r.Style.Fill.BackgroundColor = navy;
            r.Style.Alignment.WrapText = true;
        }
        var sum = wb.AddWorksheet("Summary");
        sum.Cell(1, 1).Value = $"ENPPI Workload Forecast - {s.Cycle.Name}";
        sum.Cell(1, 1).Style.Font.Bold = true; sum.Cell(1, 1).Style.Font.FontSize = 14; sum.Cell(1, 1).Style.Font.FontColor = navy;
        sum.Cell(2, 1).Value = $"Status {s.Cycle.Status}; window {s.Cycle.FirstMonth:MMM-yy} + {s.Cycle.MonthCount} months; generated {DateTime.UtcNow:dd-MMM-yyyy HH:mm} UTC";
        var kpi = new (string, double)[]
        {
            ("Planning Input MH", depts.Sum(d => d.Planning)), ("Discipline-adjusted MH", depts.Sum(d => d.Discipline)),
            ("Final (approved) MH", depts.Sum(d => d.Final)), ("After Implementation MH", depts.Sum(d => d.After)),
            ("Gross increase requested", depts.Sum(d => d.GrossIncrease)), ("Gross reduction requested", depts.Sum(d => d.GrossReduction)),
            ("Net deviation requested", depts.Sum(d => d.Net)),
        };
        for (int i = 0; i < kpi.Length; i++)
        {
            sum.Cell(4 + i, 1).Value = kpi[i].Item1;
            sum.Cell(4 + i, 2).Value = kpi[i].Item2;
            sum.Cell(4 + i, 2).Style.NumberFormat.Format = "#,##0";
        }
        sum.Column(1).Width = 30; sum.Column(2).Width = 16;

        var dws = wb.AddWorksheet("Departments");
        Header(dws, 1, "Dept", "Department", "Review status", "Planning MH", "Discipline MH", "Final MH", "After MH", "Gross Increase",
               "Gross Reduction", "Net", "Deviation %", "Concern Items", "Capacity MH/month", "Utilisation - Discipline",
               "Firm Utilisation - Discipline", "Firm Overload Months", "Implementation Rate");
        int row = 2;
        foreach (var d in depts.OrderByDescending(d => Math.Abs(d.Net)))
        {
            object[] v = { d.DeptCode, d.Department, d.ReviewStatus, d.Planning, d.Discipline, d.Final, d.After, d.GrossIncrease,
                           d.GrossReduction, d.Net, d.DeviationPct, d.ConcernItems, d.CapacityPerMonth, d.UtilisationDiscipline,
                           d.FirmUtilisationDiscipline, d.OverloadMonths, d.ImplementationRate };
            for (int i = 0; i < v.Length; i++) dws.Cell(row, i + 1).Value = XLCellValue.FromObject(v[i]);
            row++;
        }
        dws.Range(2, 4, row, 10).Style.NumberFormat.Format = "#,##0;(#,##0);-";
        dws.Range(2, 11, row, 11).Style.NumberFormat.Format = "0.0%";
        dws.Range(2, 13, row, 13).Style.NumberFormat.Format = "#,##0";
        dws.Range(2, 14, row, 15).Style.NumberFormat.Format = "0%";
        dws.Range(2, 17, row, 17).Style.NumberFormat.Format = "0%";

        var pws = wb.AddWorksheet("Projects");
        Header(pws, 1, "Project", "Name", "Severity", "Points", "Depts Raising", "Departments", "Planning MH", "Discipline MH",
               "Gross Increase", "Gross Reduction", "Net", "Months Affected", "Concern Types", "In Project Master");
        row = 2;
        foreach (var p in projects)
        {
            object[] v = { p.ProjectCode, p.Name, p.Level, p.Points, p.DeptsRaising, p.Departments, p.Planning, p.Discipline, p.GrossIncrease,
                           p.GrossReduction, p.Net, p.MonthsAffected, p.Types, p.InMaster ? "Yes" : "No" };
            for (int i = 0; i < v.Length; i++) pws.Cell(row, i + 1).Value = XLCellValue.FromObject(v[i]);
            row++;
        }
        pws.Range(2, 7, row, 11).Style.NumberFormat.Format = "#,##0;(#,##0);-";

        var rws = wb.AddWorksheet("Deviation Register");
        var list = register.ToList();
        if (list.Count > 0)
        {
            var props = list[0].GetType().GetProperties();
            Header(rws, 1, props.Select(p => p.Name).ToArray());
            row = 2;
            foreach (var item in list)
            {
                for (int i = 0; i < props.Length; i++) rws.Cell(row, i + 1).Value = XLCellValue.FromObject(props[i].GetValue(item));
                row++;
            }
        }
        foreach (var ws in wb.Worksheets)
        {
            ws.SheetView.FreezeRows(ws.Name == "Summary" ? 0 : 1);
            ws.Style.Font.FontName = "Arial";
            ws.Style.Font.FontSize = 9;
            if (ws.Name != "Summary") ws.Columns().AdjustToContents(1, 60);
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
