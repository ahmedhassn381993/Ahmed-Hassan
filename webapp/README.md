# ENPPI Workload Forecast – web application

This application replaces the email-and-Excel round of the workload forecast with one shared screen per role:

1. Planners enter or import the forecast.
2. Each discipline proposes changes against the planner value, with a reason.
3. Corporate Planning approves, partly approves, returns or rejects each request.
4. Planners carry the approved changes into Primavera.

Every number is stored once per cycle × department × project × month. The three scenarios of the comments analysis are
kept side by side:

| Scenario | Colour | Stored as |
|---|---|---|
| Planning Input | blue | `ForecastValue.PlannerMH` |
| Discipline Comment | orange | `ProposalValue.ProposedMH`. A blank month keeps the planner value; 0 removes the work. |
| Approved / After Implementation | green | `ForecastValue.ApprovedMH` and `ForecastValue.AfterMH` (the Primavera re-extract) |

The rules are the same as the comments analysis engine (`comments_analysis/engine`):

* blank = no change;
* full project codes;
* phase moves as category-change requests;
* gross, net and bridge kept separately;
* severity points with the 50k tier;
* capacity = (ROUNDUP(permanent × 0.9) + temporary) × 150 MH.

They were cross-checked on the Q3 2026 data. Loaded through the import files, the application reproduces the engine to the man-hour:

| Figure | Value |
|---|---|
| Planning Input | 6,578,735 |
| Discipline-adjusted | 7,137,827 |
| After Implementation | 7,960,161 |
| Net | +559,092 |
| Bridge buckets | identical |
| Severity | 16 Critical / 21 High |

## What is in it

| Screen | Who | What it does |
|---|---|---|
| Dashboard | everyone | • Three scenarios by month against capacity<br>• Net-deviation bridge<br>• Deviation by department<br>• Top projects by severity<br>• Review status |
| Discipline Review | discipline managers (Piping sees 2410–2450) | • Planner value (blue) with an editable proposed row (orange)<br>• Mandatory reason<br>• Quick actions: keep, zero, shift ±1 month, extend to a month<br>• Capacity strip<br>• New-allocation and category-change requests<br>• Submit, or confirm "no change" |
| My Projects | planners | • Planner grid, editable during planner entry; add lines<br>• Implementation list of approved changes ("mark implemented") |
| Approval Queue | Corporate Planning | • Requests sorted by severity, with month-by-month planner / proposed / approve values<br>• Approve, approve edited values, return (comment required) or reject<br>• Bulk-approve small changes |
| Cycle Control | Corporate Planning | • Stage stepper: Draft → Planner entry → Discipline review → Approval → Implementation → Locked<br>• Due dates<br>• Status board of the 26 departments, with return and reminders<br>• Imports with templates<br>• Reminder log and audit trail |
| Reports | everyone in scope | • Department comparison, project severity and the deviation register<br>• Excel report<br>• Power BI data (.zip) |
| Administration | administrators / Corporate Planning | • Users and department scope<br>• Headcount (capacity)<br>• Thresholds<br>• Project and department master imports |

The server enforces every rule on each call, not only the screen:

* role and department scope;
* cycle stage;
* Loan To and Internal values are Corporate Planning controlled;
* a reason is required, and changes above 20 % or 2,000 MH need a fuller justification;
* nothing can be locked while requests wait for a decision;
* no edits after lock.

Every change is written to the audit trail.

## Run it locally

You need the .NET 8 SDK and Node 22.

```bash
cd webapp/frontend && npm ci && npm run build          # builds the screens into the API's wwwroot
cd ../src/Enppi.Workload.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile --urls http://localhost:5080
```

Open http://localhost:5080 and pick a demonstration user. In development the app uses SQLite (`workload-dev.db`) and
seeds a fictional cycle: invented projects, users and man-hours, with ENPPI's department codes.

During screen development, run `npm run dev` in `frontend` (port 5173, with the API proxied to 5080).

## Tests

```bash
cd webapp && dotnet test                               # 31 tests: rules + the full workflow over HTTP as each role
cd e2e && npm ci && npx playwright test                # 6 browser tests: review → decision → implementation → reports
```

The browser tests start the app if it is not running and save screenshots of each screen in `e2e/screenshots/`.

## Load real data (from today's Excel files)

The comments analysis engine reads the legacy workbooks: To Departments, the department `-c` files and after
implementation. It writes the application's import files:

```bash
python comments_analysis/engine/run_analysis.py --cycle "Q3 2026" ... --webapp-export <folder>
```

The folder then holds four files:

* `planning.xlsx`
* `proposals.xlsx` (only the months a department changed; a phase move becomes a category-change request)
* `after.xlsx`
* `departments.csv`

Load them as follows:

1. In **Administration → Reference data import**, load `departments.csv` and the project master. The `PROJECTS` export
   works as it is.
2. In **Cycle Control**, create the cycle.
3. In **Cycle Control → Import data**, load planning, proposals and after.

Each import uses one layout: Dept, SubDept, Category, ProjectCode, Activity, Planner, Reason, Occurrence,
RequestedCategory, then one column per month. Download a blank template per cycle from the same page.

## Power BI

**Reports → Power BI data (.zip)** writes the same star schema as the comments analysis engine (FactWorkload,
FactCommentItems, Dim*…). So the Power BI template reads application data and engine output alike. The template is
`Workload_Comments_Analysis.pbit`, built by `comments_analysis/powerbi/build_pbit.py` and delivered with the analysis
outputs, not stored in this repository.

On Azure SQL, `deploy/sql/02_reporting_views.sql` adds read-only reporting views:

* `vwForecastScenarios` (same columns as FactWorkload)
* `vwDisciplineWorkload`
* `vwCommentItems`
* `vwPendingRevisions`
* `vwChangeHistory`

Give the Power BI service identity `SELECT` on schema `reporting` only.

## Deploy to Azure

`deploy/infra/main.bicep` creates one environment:

* App Service (Linux, .NET 8) with a managed identity and App Service Authentication;
* Azure SQL with Entra-only authentication (no SQL passwords);
* Application Insights.

1. **App registration** (Entra ID → App registrations):
   * Add the web redirect URI `https://<app>.azurewebsites.net/.auth/login/aad/callback`.
   * Under App roles, add `CorporatePlanning`, `Planner`, `DisciplineManager`, `Viewer` and `Admin`.
   * Assign the ENPPI groups to them (Enterprise applications → Users and groups).
   * Create a client secret.
2. **Infrastructure:**
   ```bash
   az deployment group create -g <rg> -f deploy/infra/main.bicep \
     -p environment=uat entraClientId=<app id> sqlAdminGroupName=<SQL admin group> sqlAdminGroupObjectId=<group id>
   ```
   Put the client secret in the app setting `MICROSOFT_PROVIDER_AUTHENTICATION_SECRET`, preferably as a Key Vault reference.
3. **Database access:** as the SQL admin group, run `deploy/infra/postdeploy.sql`. It grants the web app's managed
   identity access. The schema is created by EF migrations at start-up, or by the DBA from `deploy/sql/01_schema.sql`
   with `Database:MigrateOnStartup=false`.
4. **Application:** run `deploy/publish.sh`, then
   `az webapp deploy -g <rg> -n <app>-web --src-path artifacts/workload-portal.zip --type zip`. Alternatively:
   * use `deploy/azure-pipelines.yml` (UAT, then production after approval);
   * build the `Dockerfile` for container hosting.
5. **Discipline managers' scope:** in Administration → Users, add each discipline manager with their department codes,
   for example `2410,2420,2430,2440,2450` for Piping. Their role comes from the app role.
6. **Reminders:** set `Notifications:Smtp:Host` to ENPPI's relay. Without a relay, reminders are recorded on Cycle
   Control but not mailed.

### Configuration

| Setting | Production | Development |
|---|---|---|
| `Database:Provider` | `SqlServer` | `Sqlite` |
| `ConnectionStrings:Workload` | `…;Authentication=Active Directory Managed Identity;…` | `Data Source=workload-dev.db` |
| `Auth:Mode` | `EasyAuth` (refuses to start unless App Service Authentication is on) | `Development` (pick a user) |
| `Seed:Demo` | `false` | `true` |
| `Notifications:*` | SMTP relay, sender, interval | – |

Thresholds are changed in Administration → Settings: MH per man-month, effectiveness, bulk approval, justification and
reminder days.

## Security notes

* **Sign-in.** App Service Authentication signs users in with their ENPPI account. The API reads the platform's
  `X-MS-CLIENT-PRINCIPAL` header, which App Service strips from incoming requests. In `EasyAuth` mode the app refuses
  to start if platform authentication is off.
* **Roles and scope.** Roles come from Entra app roles. Department scope comes from the Users table. Every endpoint
  checks both.
* **Database credentials.** The database is reached through the managed identity; no password exists in code or
  configuration.
* **Endpoints.** The development sign-in endpoints exist only in `Development` mode. Swagger is off in production.

## Not yet verified here

This environment had no SQL Server, Docker daemon or Azure subscription. These were checked as follows:

* **SQL Server migration and reporting views:** generated, and the views parsed as T-SQL.
* **Docker image:** not built.
* **Bicep template:** compiled.

The application itself ran end to end on SQLite in development mode and in production mode. Run the UAT deployment
before go-live.
