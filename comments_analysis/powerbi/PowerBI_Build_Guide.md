# Workload Comments Analysis – Power BI build guide

This report compares three scenarios in the same format as the existing workload Power BI
(1280 × 720 pages, ENPPI logo top-left, "Company Workload Forecast" title, department card top-right,
status slicer bottom-right):

| Scenario | Meaning | Colour |
|---|---|---|
| Planning Input | Man-hours sent to departments ("To Departments") | Blue `#2A78D6` |
| Discipline Comment | Planning Input with each populated, different department value applied (blank = no revision) | Orange `#EB6834` |
| After Implementation | Forecast re-extracted after planners implemented the comments | Aqua-green `#1BAF7A` |

It replaces the current *Forecast Comments* PBIX, which compares only two folders (`To Departments`,
`COMMENTS`) against the Primavera export table, and replaces the Copilot 3-scenario workbook (its defects are
listed in the analysis workbook, sheet *Data Quality Log*).

## 1. Produce the data (each cycle)

```text
python engine/run_analysis.py --cycle "Q4 2026" \
    --before "<cycle folder>/To Departments" --comments "<cycle folder>/COMMENTS" --after "<cycle folder>/after" \
    --employees employee_count.xlsx --checklist "Critical Projects - Workload Forecast Comments Checklist.xlsx" \
    --out "<OutputRoot>/Q4_2026"
```

The run writes the analysis workbook plus `powerbi/*.csv` and `powerbi/Workload_Comments_PowerBI_Model.xlsx`.
Keep one sub-folder per cycle under `OutputRoot`; the queries load every cycle.

## 2. Load the model (Power BI Desktop)

**Quick start (one cycle).** *Get data → Excel workbook →* `Workload_Comments_PowerBI_Model.xlsx`, then tick all
ten tables. Column names match, so most relationships are detected automatically. Check them against the
table below.

**Recommended (all cycles, refreshable).**
1. *Transform data → Manage parameters → New*: `OutputRoot` (Text) = the shared output folder.
2. Create each query from `queries.pq` (Blank query → Advanced editor). Create `fnLoadCsv` first.
3. *Close & apply.*

Codes are loaded as text on purpose: `5305200`, `40061420` and `5020250-7` must never become numbers.

## 3. Relationships

| From (many) | To (one) | Cross-filter |
|---|---|---|
| FactWorkload[Scenario] | DimScenario[Scenario] | Single |
| FactWorkload[DeptCode] | DimDepartment[DeptCode] | Single |
| FactWorkload[Category] | DimCategory[Category] | Single |
| FactWorkload[ProjectCode] | DimProject[ProjectCode] | Single |
| FactWorkload[Month] | DimDate[Month] | Single |
| FactCommentItems[DeptCode] | DimDepartment[DeptCode] | Single |
| FactCommentItems[Category] | DimCategory[Category] | Single |
| FactCommentItems[ProjectCode] | DimProject[ProjectCode] | Single |
| FactCellChanges[CycleRowKey] | FactCommentItems[CycleRowKey] | Single |
| FactCellChanges[Month] | DimDate[Month] | Single |
| BridgeConcernType[CycleRowKey] | FactCommentItems[CycleRowKey] | **Both** (lets a Concern Type slicer filter items) |

Sort `DimScenario[Scenario]` by `Scenario Order`, `DimCategory[Category]` by `Category Order`,
`DimDate[Month Label]` by `Month`, `DimProject[Severity]` by `Severity Order`.
Mark `DimDate` as a date table (column `Month`).

## 4. Measures and theme

* Create a table `_Measures` and paste the measures from `measures.dax` (display folders suggested in the file).
* *View → Themes → Browse for themes →* `ENPPI_Workload_Theme.json`. With Scenario on the legend (sorted by
  Scenario Order), Planning is blue, Discipline orange and After Implementation green automatically.
* Category colours (same as the web application, Excel and PDF): Running `#2E7D32`, Internal / Unforeseen `#00897B`,
  Loan To `#607D8B`, Under Signature `#6A1B9A`, Proposal `#EF6C00`, Potential Opportunity `#F9A825`.

## 5. Pages

Common header on every page, copied from the existing report: ENPPI logo (x 0, y 0), title text box
"Company Workload Forecast – Comments Analysis", an "as of" card (`As Of Label`), department card
(`Selected Department`), and a Category chiclet slicer bottom-right (replaces `Projects.Status_code`).
Add a **Cycle** slicer (single select) on every page.

1. **Comments Overview.** Eight cards: Planning MH, Discipline MH, Implemented MH, Disc vs Plan MH,
   Disc vs Plan %, Implementation Rate, Concern Items, New Project Rows. Line chart: axis `DimDate[Month]`,
   values Planning MH, Discipline MH, Implemented MH, Capacity MH (one axis, MH). Bar chart: Department ×
   Disc vs Plan MH, sorted descending, bar colour from `Deviation Colour`.
2. **Workload Comparison – MHRs** (same layout as today's page). Left: attribute slicer *Difference by Department*
   (`DimDepartment[Dept Label]`, value `ABS_Variance`). Bottom-left: *Difference by Project*
   (`DimProject[Project Label]`, `ABS_Variance`). Main: line chart of the three scenarios by month with tooltips
   Discipline MM and ABS_Variance_MMonth. The current report puts the variance on a second axis; here it goes in a
   separate column chart underneath (`Disc vs Plan MH` by month), because two scales on one chart mislead.
   Bottom-right: Timeline slicer on Month and the Category chiclet slicer.
3. **Department Comparison.** Matrix with `Dept Label` rows and the measures Planning, Discipline, Implemented,
   Disc vs Plan MH/%, Concern Items, Implementation Rate, Utilisation Discipline, Firm Utilisation Discipline, Overload
   Months. Conditional formatting: deviation diverging (blue / grey / orange), utilisation background above 100%.
   Second matrix: Dept Label × Month Label, value `Utilisation Discipline` (heat map).
4. **Project Concerns.** Table from DimProject (Project Label, Severity, Severity Points, Depts Concerned,
   Departments Raising) plus Planning, Discipline, Disc vs Plan MH, Implementation Rate; background on Severity
   via `Severity Colour`. Matrix Project Label × Dept Label, value Disc vs Plan MH. Slicers: Severity, Concern Type
   (`BridgeConcernType[ConcernType]`).
5. **Added Projects** (same idea as today's page). Matrix: rows `DimDepartment[Dept Label]` → `DimProject[Project Label]`,
   columns `DimDate[Month Label]`, value Discipline MH; page filter Concern Type contains "New".
   Add a card with New Project MH.
6. **Implementation Tracking.** Stacked bar: Department × Items Implemented / Partially / Not Implemented.
   Table: FactCommentItems filtered to Concern = Yes and Implementation Status starts with "Not implemented"
   (Dept, Project, Activity, Planning MH, Discipline MH, Implemented MH, Concern Types). Card: Planner Change Without
   Comment MH.
7. **Capacity & Overload.** Line chart of Utilisation Planning / Discipline / Implemented by month with a constant
   line at 100%. Matrix Dept × Month of Utilisation Discipline. Card: Extra Heads Needed (Peak).
8. **Cell Detail (drill-through).** Drill-through field `DimProject[ProjectCode]` and `DimDepartment[DeptCode]`.
   Table from FactCellChanges: Month, Planning MH, Discipline Value, Discipline MH, Change MH, Implemented MH,
   Cell Status, Blank Note.
9. **Data Quality.** Table of DataQuality sorted by Severity, slicer on Area.

## 6. Security and publishing

Publish to a Power BI workspace for Corporate Planning. To give each discipline manager their own view,
add a row-level security role on `DimDepartment[DeptCode]` (or `Division`) mapped to Entra ID groups.
This is the same rule the web application will enforce.

## 7. Moving to the web application

When the web application goes live, keep this report and repoint the queries to the SQL reporting views
(`reporting.vwForecastScenarios`, `reporting.vwCommentItems`). Scenario names stay the same:
Planning Input = PlannerManhours, Discipline Comment = ProposedManhours (applied), After Implementation =
ApprovedManhours / final.
