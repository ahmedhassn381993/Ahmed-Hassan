# ENPPI Workload Forecast – Discipline Comments Analysis

Compares the three versions of each forecast round:

| Scenario | Source folder | Meaning |
|---|---|---|
| Planning Input | `To Departments` (before) | Man-hours sent to each department |
| Discipline Comment | `COMMENTS` (`<dept>-c.xlsx`) | Department feedback; a blank cell means no change |
| System Implementation | `after` | Forecast re-extracted after planners implemented comments |

## Run

```text
pip install pandas openpyxl
python engine/run_analysis.py --cycle "Q4 2026" \
    --before "<round>/To Departments" --comments "<round>/COMMENTS" --after "<round>/after" \
    --employees employee_count.xlsx \
    --checklist "Critical Projects - Workload Forecast Comments Checklist.xlsx" \
    --out output/Q4_2026 [--note "context sentence for the implementation finding"]
```

`--copilot-fact <PowerBI_Workload_Model_3_Scenarios...xlsx>` can stand in for missing folders; its
defects are corrected and logged (used for Q3 2026 because the before/after workbooks were not uploaded).

Outputs in `--out`:

* `ENPPI_Workload_Comments_Analysis_<cycle>.xlsx`: executive summary, department and project ranking,
  monthly impact and capacity, deviation register, cell changes, new projects, phase transfers and
  rescheduling, implementation tracking, auto-filled critical-projects checklist, data quality log, method.
* `powerbi/`: star-schema CSV files and `Workload_Comments_PowerBI_Model.xlsx` for the Power BI report
  (build steps in `powerbi/PowerBI_Build_Guide.md`).
* `issues_log.csv`: every correction and open data issue.

`output/` is git-ignored: results contain ENPPI project, client and headcount data.

## Rules applied

* Match key: department + sub-department + phase + full project code + month. Codes come from the
  activity text because the template's `=LEFT(B,7)` truncates 8-digit and suffixed codes.
* Blank feedback = keep the planning value; a populated, different value = change; deviation = feedback − planning.
* Rows only in feedback = new allocation; rows missing from feedback are kept, never treated as removed.
* A project found under another phase in the feedback is paired with its baseline row (no double counting)
  and reported as a phase transfer.
* Department/sub-department total rows are never counted as projects.

## Layout

```text
engine/workbook_parser.py   reads one department workbook (values, blanks, red/fill marks)
engine/build_dataset.py     builds the clean three-scenario table; corrects the Copilot table
engine/compare.py           three-way comparison, classification, implementation status, severity
engine/analysis.py          department/project/monthly summaries, capacity, checklist
engine/report_excel.py      management workbook (formula-driven summaries)
engine/powerbi_export.py    Power BI star schema
engine/run_analysis.py      command line entry point
powerbi/                    Power Query, DAX measures, theme, build guide
output/Q3_2026/             results for the Q3 2026 round
```
