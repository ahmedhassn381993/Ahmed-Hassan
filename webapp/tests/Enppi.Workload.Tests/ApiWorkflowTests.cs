using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Enppi.Workload.Tests;

/// <summary>Runs the real application (SQLite file, development sign-in, demo seed) and drives the workflow over HTTP.</summary>
public class AppFactory : WebApplicationFactory<Program>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"workload-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting applies before Program.cs reads the configuration (ConfigureAppConfiguration would be too late)
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("ConnectionStrings:Workload", $"Data Source={_db}");
        builder.UseSetting("Auth:Mode", "Development");
        builder.UseSetting("Seed:Demo", "true");
        builder.UseSetting("Notifications:Enabled", "false");
    }

    public HttpClient As(string upn)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Dev-User", upn);
        c.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");   // the screens' CSRF marker
        return c;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(_db); } catch (IOException) { }
    }
}

public class ApiWorkflowTests : IClassFixture<AppFactory>
{
    private const string Cp = "cp.lead@enppi.demo", Elec = "gm.electrical@enppi.demo", Process = "gm.process@enppi.demo",
        Planner1 = "planner.one@enppi.demo", Viewer = "viewer@enppi.demo";
    private readonly AppFactory _app;
    private static readonly int ImportColumns = Enppi.Workload.Api.Services.ImportService.ForecastColumns.Length;
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

    public ApiWorkflowTests(AppFactory app) => _app = app;

    private static async Task<JsonNode> Json(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode} {text}");
        return JsonNode.Parse(text)!;
    }

    private async Task<int> NewCycleAsync(string name, string status = "DisciplineReview")
    {
        var cp = _app.As(Cp);
        var c = await Json(await cp.PostAsJsonAsync("/api/cycles", new { name, firstMonth = "2027-01-01", monthCount = 12 }));
        int id = c["id"]!.GetValue<int>();
        // planning input through the import template, as Corporate Planning would load it
        var template = await (await cp.GetAsync($"/api/cycles/{id}/templates/planning")).Content.ReadAsByteArrayAsync();
        using var wb = new XLWorkbook(new MemoryStream(template));
        var ws = wb.Worksheet("Forecast");
        void Row(int r, string dept, string cat, string code, string act, string planner, params double[] months)
        {
            ws.Cell(r, 1).Value = dept; ws.Cell(r, 2).Value = dept; ws.Cell(r, 3).Value = cat; ws.Cell(r, 4).Value = code;
            ws.Cell(r, 5).Value = act; ws.Cell(r, 6).Value = planner;
            for (int m = 0; m < months.Length; m++) ws.Cell(r, ImportColumns + 1 + m).Value = months[m];
        }
        Row(2, "2500", "Running", "9100100", "Gas plant", Planner1, 100, 100, 100, 100);
        Row(3, "2500", "Loan To", "9100200-1", "Site support", Planner1, 50, 50);
        Row(4, "1100", "Proposal", "9100500", "Fertilizer FEED", Planner1, 0, 200, 200);
        Row(5, "2500", "Running", "9100300", "Offshore tie-in", Planner1, 1000, 1000, 1000, 1000);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var form = new MultipartFormDataContent { { new ByteArrayContent(ms.ToArray()), "file", "planning.xlsx" } };
        var res = await Json(await cp.PostAsync($"/api/cycles/{id}/import/planning", form));
        Assert.Equal(4, res["created"]!.GetValue<int>());
        if (status != "Draft") await Json(await cp.PostAsJsonAsync($"/api/cycles/{id}/status", new { status }));
        return id;
    }

    private async Task<JsonArray> LinesAsync(int cycle, string dept) =>
        (await Json(await _app.As(Cp).GetAsync($"/api/cycles/{cycle}/lines?dept={dept}"))).AsArray();

    private static object Values(params (int m, double? v)[] values) => values.Select(x => new { monthIndex = x.m, value = x.v }).ToList();

    [Fact]
    public async Task Unauthenticated_calls_are_refused()
    {
        var r = await _app.CreateClient().GetAsync("/api/cycles");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Full_cycle_from_proposal_to_lock_and_power_bi_export()
    {
        int id = await NewCycleAsync("Test full cycle");
        var lines = await LinesAsync(id, "2500");
        int gas = lines.First(l => (string)l!["project"]! == "9100100")!["id"]!.GetValue<int>();
        var elec = _app.As(Elec);

        // a change with no reason cannot be submitted
        await Json(await elec.PostAsJsonAsync($"/api/cycles/{id}/proposals",
            new { lineId = gas, deptCode = "2500", kind = "Change", reason = "", values = Values((1, 150), (4, 80)) }));
        var bad = await elec.PostAsync($"/api/cycles/{id}/review/2500/submit", null);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("reason is required", await bad.Content.ReadAsStringAsync());

        // with a reason it goes through; month 4 is beyond the planned finish (extension)
        var saved = await Json(await elec.PostAsJsonAsync($"/api/cycles/{id}/proposals",
            new { lineId = gas, deptCode = "2500", kind = "Change", reason = "Client added a second substation", values = Values((1, 150), (4, 80)) }));
        Assert.Contains("Schedule Extension", (string)saved["changeTypes"]!);
        // a new allocation request for a project the department does not have yet
        await Json(await elec.PostAsJsonAsync($"/api/cycles/{id}/proposals",
            new { deptCode = "2500", kind = "NewAllocation", reason = "Proposal team needs a load list", newProjectCode = "9100900",
                  newActivity = "Desalination", newCategory = "Proposal", values = Values((2, 300), (3, 300)) }));
        // a 32% change and a new allocation need a fuller justification than a few words
        var thin = await elec.PostAsync($"/api/cycles/{id}/review/2500/submit", null);
        Assert.Equal(HttpStatusCode.BadRequest, thin.StatusCode);
        Assert.Contains("fuller justification", await thin.Content.ReadAsStringAsync());
        await Json(await elec.PostAsJsonAsync($"/api/cycles/{id}/proposals",
            new { lineId = gas, deptCode = "2500", kind = "Change", values = Values((1, 150), (4, 80)),
                  reason = "Client added a second substation in May; cable schedules and relay settings run one month past the plan" }));
        var requests = (await Json(await elec.GetAsync($"/api/cycles/{id}/review/2500")))["newAllocations"]!.AsArray();
        await Json(await elec.PostAsJsonAsync($"/api/cycles/{id}/proposals",
            new { proposalId = requests[0]!["id"]!.GetValue<int>(), deptCode = "2500", kind = "NewAllocation", newProjectCode = "9100900",
                  newActivity = "Desalination", newCategory = "Proposal", values = Values((2, 300), (3, 300)),
                  reason = "Proposal team needs the electrical load list and single-line diagram for the bid" }));
        var sub = await Json(await elec.PostAsync($"/api/cycles/{id}/review/2500/submit", null));
        Assert.Equal("Submitted", (string)sub["status"]!);
        Assert.Single((await Json(await elec.GetAsync($"/api/cycles/{id}/review/2500")))["newAllocations"]!.AsArray());

        // once submitted the department cannot keep editing
        var late = await elec.PostAsJsonAsync($"/api/cycles/{id}/proposals",
            new { lineId = gas, deptCode = "2500", kind = "Change", reason = "late", values = Values((0, 10)) });
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);

        // discipline-adjusted = planning + 50 + 80 + 600 (new); final unchanged until approval
        var dash = await Json(await _app.As(Cp).GetAsync($"/api/cycles/{id}/dashboard"));
        double planning = dash["kpis"]!["planning"]!.GetValue<double>();
        Assert.Equal(planning + 730, dash["kpis"]!["discipline"]!.GetValue<double>(), 3);
        Assert.Equal(planning, dash["kpis"]!["final"]!.GetValue<double>(), 3);
        var bridge = dash["bridge"]!;
        Assert.Equal(730, bridge["newWork"]!.GetValue<double>() + bridge["increase"]!.GetValue<double>() + bridge["extension"]!.GetValue<double>()
                          + bridge["reduction"]!.GetValue<double>() + bridge["removed"]!.GetValue<double>(), 3);

        // Corporate Planning: lock is refused while decisions are pending; returning needs a comment
        var cp = _app.As(Cp);
        var queue = (await Json(await cp.GetAsync($"/api/cycles/{id}/queue"))).AsArray();
        Assert.Equal(2, queue.Count);
        var lockEarly = await cp.PostAsJsonAsync($"/api/cycles/{id}/status", new { status = "Locked" });
        Assert.Equal(HttpStatusCode.BadRequest, lockEarly.StatusCode);
        int change = queue.First(q => (string)q!["project"]! == "9100100")!["proposal"]!["id"]!.GetValue<int>();
        int request = queue.First(q => (string)q!["project"]! == "9100900")!["proposal"]!["id"]!.GetValue<int>();
        Assert.Equal(HttpStatusCode.BadRequest, (await cp.PostAsJsonAsync($"/api/proposals/{change}/decision", new { action = "return" })).StatusCode);

        // partial approval of the change, full approval of the new work
        var dec = await Json(await cp.PostAsJsonAsync($"/api/proposals/{change}/decision",
            new { action = "partial", comment = "Extension accepted, peak trimmed", approvedValues = Values((1, 120)) }));
        Assert.Equal("PartiallyApproved", (string)dec["status"]!);
        await Json(await cp.PostAsJsonAsync($"/api/proposals/{request}/decision", new { action = "approve", comment = "Agreed" }));
        dash = await Json(await cp.GetAsync($"/api/cycles/{id}/dashboard"));
        Assert.Equal(planning + 20 + 80 + 600, dash["kpis"]!["final"]!.GetValue<double>(), 3);

        // planner marks the change implemented; then the cycle locks and planner values are frozen
        var impl = (await Json(await _app.As(Planner1).GetAsync($"/api/cycles/{id}/implementation"))).AsArray();
        Assert.Contains(impl, x => (string)x!["project"]! == "9100100");
        Assert.Equal(HttpStatusCode.NoContent, (await _app.As(Planner1).PostAsync($"/api/proposals/{change}/implemented", null)).StatusCode);
        await Json(await cp.PostAsJsonAsync($"/api/cycles/{id}/status", new { status = "Locked" }));
        var frozen = await _app.As(Planner1).PutAsJsonAsync($"/api/cycles/{id}/lines/{gas}/planner-values", new { values = Values((0, 1)) });
        Assert.True(frozen.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest);

        // Power BI export: the engine's star schema, totals equal to the dashboard
        var zipBytes = await (await _app.As(Viewer).GetAsync($"/api/cycles/{id}/export/powerbi")).Content.ReadAsByteArrayAsync();
        using var zip = new ZipArchive(new MemoryStream(zipBytes));
        var names = zip.Entries.Select(e => Path.GetFileName(e.FullName)).ToHashSet();
        foreach (var f in new[] { "FactWorkload.csv", "FactCommentItems.csv", "FactCellChanges.csv", "BridgeConcernType.csv", "DimDepartment.csv",
                                  "DimProject.csv", "DimCategory.csv", "DimScenario.csv", "DimDate.csv", "DataQuality.csv", "FileInventory.csv",
                                  "PossibleTransfers.csv" })
            Assert.Contains(f, names);
        var fact = new StreamReader(zip.Entries.First(e => e.Name == "FactWorkload.csv").Open(), Encoding.UTF8).ReadToEnd()
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Split(',')).ToList();
        double Sum(string scen) => fact.Where(c => c[1] == scen).Sum(c => double.Parse(c[7], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(planning, Sum("Planning Input"), 3);
        Assert.Equal(planning + 730, Sum("Discipline Comment"), 3);
        Assert.Equal(planning + 700, Sum("After Implementation"), 3);   // no after extract yet: approved values

        // Excel report opens
        var xlsx = await (await cp.GetAsync($"/api/cycles/{id}/export/excel")).Content.ReadAsByteArrayAsync();
        using var report = new XLWorkbook(new MemoryStream(xlsx));
        Assert.True(report.Worksheets.Contains("Deviation Register"));
    }

    [Fact]
    public async Task Departments_are_limited_to_their_own_scope_and_loan_to_is_read_only()
    {
        int id = await NewCycleAsync("Test scope");
        var lines = await LinesAsync(id, "2500");
        int gas = lines.First(l => (string)l!["project"]! == "9100100")!["id"]!.GetValue<int>();
        int loan = lines.First(l => (string)l!["category"]! == "LoanTo")!["id"]!.GetValue<int>();

        var other = await _app.As(Process).PostAsJsonAsync($"/api/cycles/{id}/proposals",
            new { lineId = gas, deptCode = "2500", kind = "Change", reason = "x", values = Values((0, 1)) });
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _app.As(Process).GetAsync($"/api/cycles/{id}/review/2500")).StatusCode);

        var loanTo = await _app.As(Elec).PostAsJsonAsync($"/api/cycles/{id}/proposals",
            new { lineId = loan, deptCode = "2500", kind = "Change", reason = "x", values = Values((0, 10)) });
        Assert.Equal(HttpStatusCode.BadRequest, loanTo.StatusCode);
        Assert.Contains("Corporate Planning controlled", await loanTo.Content.ReadAsStringAsync());

        // a discipline manager sees only their own department's lines
        var mine = (await Json(await _app.As(Process).GetAsync($"/api/cycles/{id}/lines"))).AsArray();
        Assert.All(mine, l => Assert.Equal("1100", (string)l!["dept"]!));
        // approvals are Corporate Planning only
        Assert.Equal(HttpStatusCode.Forbidden, (await _app.As(Elec).PostAsJsonAsync("/api/proposals/1/decision", new { action = "approve" })).StatusCode);
    }

    [Fact]
    public async Task No_change_confirmation_and_bulk_approval_of_small_changes()
    {
        int id = await NewCycleAsync("Test bulk");
        var proc = await Json(await _app.As(Process).PostAsync($"/api/cycles/{id}/review/1100/submit", null));
        Assert.Equal("NoChangeConfirmed", (string)proc["status"]!);

        // +10 MH on a 4,000 MH project is below 5% and 500 MH -> bulk approvable
        var lines = await LinesAsync(id, "2500");
        int offshore = lines.First(l => (string)l!["project"]! == "9100300")!["id"]!.GetValue<int>();
        var elec = _app.As(Elec);
        await Json(await elec.PostAsJsonAsync($"/api/cycles/{id}/proposals",
            new { lineId = offshore, deptCode = "2500", kind = "Change", reason = "Small vendor review", values = Values((0, 1010)) }));
        await Json(await elec.PostAsync($"/api/cycles/{id}/review/2500/submit", null));
        var bulk = await Json(await _app.As(Cp).PostAsync($"/api/cycles/{id}/bulk-approve", null));
        Assert.Equal(1, bulk["approved"]!.GetValue<int>());
        var board = (await Json(await _app.As(Cp).GetAsync($"/api/cycles/{id}/status-board"))).AsArray();
        Assert.Equal("NoChangeConfirmed", (string)board.First(b => (string)b!["dept"]! == "1100")!["status"]!);
    }

    [Fact]
    public async Task Planner_edits_only_own_lines_during_planner_entry()
    {
        int id = await NewCycleAsync("Test planner", "PlannerEntry");
        var lines = await LinesAsync(id, "2500");
        int gas = lines.First(l => (string)l!["project"]! == "9100100")!["id"]!.GetValue<int>();
        int loan = lines.First(l => (string)l!["category"]! == "LoanTo")!["id"]!.GetValue<int>();
        var p1 = _app.As(Planner1);
        Assert.Equal(HttpStatusCode.NoContent, (await p1.PutAsJsonAsync($"/api/cycles/{id}/lines/{gas}/planner-values", new { values = Values((5, 75)) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _app.As("planner.two@enppi.demo").PutAsJsonAsync(
            $"/api/cycles/{id}/lines/{gas}/planner-values", new { values = Values((5, 1)) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await p1.PutAsJsonAsync($"/api/cycles/{id}/lines/{loan}/planner-values", new { values = Values((0, 1)) })).StatusCode);
        var audit = (await Json(await _app.As(Cp).GetAsync($"/api/cycles/{id}/audit"))).AsArray();
        Assert.Contains(audit, a => (string)a!["action"]! == "PlannerValues");
    }

    [Fact]
    public async Task After_implementation_import_drives_implementation_status()
    {
        int id = await NewCycleAsync("Test after");
        var lines = await LinesAsync(id, "2500");
        int gas = lines.First(l => (string)l!["project"]! == "9100100")!["id"]!.GetValue<int>();
        var elec = _app.As(Elec);
        await Json(await elec.PostAsJsonAsync($"/api/cycles/{id}/proposals",
            new { lineId = gas, deptCode = "2500", kind = "Change", values = Values((0, 140)),
                  reason = "More cable routing after the plot plan change moved the substation further away" }));
        await Json(await elec.PostAsync($"/api/cycles/{id}/review/2500/submit", null));
        var cp = _app.As(Cp);
        int pid = (await Json(await cp.GetAsync($"/api/cycles/{id}/queue"))).AsArray()[0]!["proposal"]!["id"]!.GetValue<int>();
        await Json(await cp.PostAsJsonAsync($"/api/proposals/{pid}/decision", new { action = "approve", comment = "ok" }));

        var csv = "Dept,SubDept,Category,ProjectCode,Activity,Planner,Reason,2027-01-01,2027-02-01,2027-03-01,2027-04-01\n" +
                  "2500,2500,Running,9100100,Gas plant,,,140,100,100,100\n" +
                  "2500,2500,Running,9100300,Offshore tie-in,,,1000,1000,1000,1000\n" +
                  "2500,2500,Under Signature,9100400,Pipeline,,,0,50,0,0\n";
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "after.csv" } };
        var res = await Json(await cp.PostAsync($"/api/cycles/{id}/import/after", form));
        Assert.Equal(1, res["created"]!.GetValue<int>());   // 9100400 exists only after implementation
        var impl = (await Json(await cp.GetAsync($"/api/cycles/{id}/implementation"))).AsArray();
        Assert.Equal("Implemented", (string)impl.First(x => (string)x!["project"]! == "9100100")!["status"]!);
    }

    [Fact]
    public async Task Entra_principal_header_is_read_in_easy_auth_mode()
    {
        await using var easy = _app.WithWebHostBuilder(b => b.UseSetting("Auth:Mode", "EasyAuth"));
        var client = easy.CreateClient();
        var principal = new
        {
            auth_typ = "aad",
            claims = new[]
            {
                new { typ = "preferred_username", val = "Someone@ENPPI.demo" }, new { typ = "name", val = "Someone" },
                new { typ = "roles", val = "CorporatePlanning" },
            },
        };
        client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(principal))));
        var me = await Json(await client.GetAsync("/api/me"));
        Assert.Equal("someone@enppi.demo", (string)me["upn"]!);
        Assert.Contains("CorporatePlanning", me["roles"]!.AsArray().Select(r => (string)r!));
        // the development sign-in does not exist in this mode
        Assert.NotEqual(HttpStatusCode.NoContent, (await easy.CreateClient().PostAsJsonAsync("/api/dev/login", new { upn = "cp.lead@enppi.demo" })).StatusCode);
    }
}
