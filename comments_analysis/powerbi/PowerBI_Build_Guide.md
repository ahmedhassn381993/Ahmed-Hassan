# Workload Comments Analysis – Power BI guide

This report compares three scenarios in the same format as the existing workload Power BI: 1280 × 720 pages,
ENPPI logo top-left, "Company Workload Forecast" title, and slicers top-right.

| Scenario | Meaning | Colour |
|---|---|---|
| Planning Input | Man-hours sent to departments ("To Departments") | Blue `#2A78D6` |
| Discipline Comment | Planning Input with each populated, different department value applied (blank = no revision) | Orange `#EB6834` |
| After Implementation | Forecast re-extracted after planners implemented the comments | Aqua-green `#1BAF7A` |

It replaces two things:

* the current *Forecast Comments* PBIX, which compares only two folders (`To Departments` and `COMMENTS`)
  against the Primavera export table;
* the Copilot 3-scenario workbook, whose defects are listed in the analysis workbook, sheet *Data Quality Log*.

## 1. Produce the data (each cycle)

```text
python engine/run_analysis.py --cycle "Q4 2026" \
    --before "<cycle folder>/To Departments" --comments "<cycle folder>/COMMENTS" --after "<cycle folder>/after" \
    --employees employee_count.xlsx --checklist "Critical Projects - Workload Forecast Comments Checklist.xlsx" \
    --master PROJECTS_my_db.xlsx --tracker "Enppi_Workload_Forecast_Workflow_Steps (Schedule).xlsx" \
    --out "<OutputRoot>/Q4_2026"
```

The run writes the analysis workbook plus `powerbi/*.csv`. Keep one sub-folder per cycle under `OutputRoot`.
The report loads every cycle; the **Cycle** slicer on each page selects one.

## 2. Open the template (recommended)

1. Double-click `Workload_Comments_Analysis.pbit`.
2. When asked for **OutputRoot**, enter the shared output folder (the parent of `Q3_2026`, `Q4_2026`, …),
   then click **Load**.
3. Save the result as a `.pbix`. From then on, *Refresh* picks up new cycles.

The template contains:

* the data model: queries, 16 relationships, sort orders and the measures in `measures.dax`;
* the ENPPI theme;
* nine pages built only from Power BI's standard visuals, so no custom-visual downloads are needed.

Rebuild it with:

```text
python powerbi/build_pbit.py --out Workload_Comments_Analysis.pbit --pbix Forecast_Comments.pbix
```

`--pbix` copies the ENPPI logo and base theme from the existing workload report.

**First open – what to check.** The template was generated without Power BI Desktop, so on first open:

* confirm the nine pages render;
* compare the *Comments Overview* cards with the Excel *Executive Summary*.

If Desktop rejects the file, build the report by hand from section 3. That uses the same queries and measures.

## 3. Manual build (fallback)

1. *Transform data → Manage parameters → New*: `OutputRoot` (Text) = the shared output folder.
2. Create each query in `queries.pq` (Blank query → Advanced editor), named as in the block titles.
   Create `fnLoadCsv` first. `queries.pq` is generated from the same spec as the template, so the two cannot drift.
3. Create the relationships below. Then:
   * sort `DimScenario[Scenario]` by `Scenario Order`, `DimCategory[Category]` by `Category Order`,
     `DimDate[Month Label]` by `Month`, and `DimProject[Severity]` by `Severity Order`;
   * turn off *Auto date/time*.
4. Create a table `_Measures` and paste the measures from `measures.dax`. Display folders are given in [brackets].
5. *View → Themes → Browse for themes →* `ENPPI_Workload_Theme.json`.

Codes are loaded as text on purpose: `5305200`, `40061420` and `5020250-7` must never become numbers.

| From (many) | To (one) | Cross-filter |
|---|---|---|
| FactWorkload[Scenario] | DimScenario[Scenario] | Single |
| FactWorkload[DeptCode] / [Category] / [ProjectCode] / [Month] / [Cycle] | DimDepartment / DimCategory / DimProject / DimDate / DimCycle | Single |
| FactCommentItems[DeptCode] / [Category] / [ProjectCode] / [Cycle] | DimDepartment / DimCategory / DimProject / DimCycle | Single |
| FactCellChanges[CycleRowKey] | FactCommentItems[CycleRowKey] | Single |
| FactCellChanges[Month] | DimDate[Month] | Single |
| BridgeConcernType[CycleRowKey] | FactCommentItems[CycleRowKey] | **Both** (a Concern category slicer filters items) |
| DataQuality / FileInventory / PossibleTransfers [Cycle] | DimCycle[Cycle] | Single |

Column names never repeat a measure name. In the detail tables the MH columns are therefore prefixed:
`FactCommentItems[Item Planning MH]` and `FactCellChanges[Cell Discipline MH]`, for example.
The measures are named without the prefix (`Planning MH`, `Discipline MH`, `Implemented MH`).

## 4. Pages

Common header on every page:

* ENPPI logo;
* title and page name;
* **Cycle** slicer (single select);
* **Department** and **Category** slicers.

| Page | Content |
|---|---|
| 1. Comments Overview | Eight cards:<br>• Planning, Discipline and After Impl. MH<br>• net deviation MH and %<br>• implementation rate<br>• concern items<br>• feedback coverage<br>Plus:<br>• the three scenarios and capacity by month<br>• the net deviation bridge: new work, increase, extension, reduction, removed<br>• deviation by department |
| 2. Workload Comparison – MHRs | Same idea as today's page:<br>• difference by department and by project<br>• the three scenarios by month<br>• monthly deviation in its own chart (no second axis) |
| 3. Department Comparison | Department matrix:<br>• MH per scenario, deviation and concern items<br>• implementation rate, utilisation and overload months<br>Plus department × month utilisation |
| 4. Project Concerns | Slicers:<br>• Severity<br>• Concern category (the brief's 11)<br>• Project master check<br>Plus:<br>• projects ranked by severity<br>• project × department deviation |
| 5. Added Projects | New allocations by department / project / activity × month |
| 6. Implementation Tracking | • Items implemented / partially / not implemented by department<br>• rate cards<br>• the list of requests not implemented |
| 7. Capacity & Overload | • Utilisation by month with the 100% line<br>• extra heads at peak<br>• overload months<br>• legacy % by category: Running+Loan%, Internal%, Signature%, Proposal%, Potential%, WL% (same definitions as the workload report)<br>• department × month heat map |
| 8. Cell Detail | Pick a project to see:<br>• every changed month: planning, department value, final value, after implementation<br>• blanks that were retained |
| 9. Data Quality | Slicers for area and severity.<br>Tables:<br>• issues<br>• the file inventory, with tracker dates<br>• possible transfers |

After the first refresh, optionally add conditional formatting:

* the `Deviation Colour` measure on deviation bars (blue = reduction, orange = increase);
* `Severity Colour` on Severity;
* a background colour scale on the utilisation heat maps.

## 5. Security and publishing

Publish to a Power BI workspace for Corporate Planning. To give each discipline manager their own view, add a
row-level security role on `DimDepartment[DeptCode]` (or `Division`) mapped to Entra ID groups. The web
application will enforce the same rule.

## 6. Moving to the web application

When the web application goes live, keep this report. Change `fnLoadCsv` to read the SQL reporting views
(`reporting.vwForecastScenarios`, `reporting.vwCommentItems`); the column names stay the same. The scenarios map as
follows:

| Report scenario | Web application field |
|---|---|
| Planning Input | PlannerManhours |
| Discipline Comment | ProposedManhours (applied) |
| After Implementation | ApprovedManhours / final |
