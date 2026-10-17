"""Run the full discipline-comments analysis for one forecast cycle.

Example (this cycle - originals not uploaded, so the validated Copilot table fills the gaps):
    python run_analysis.py --cycle "Q3 2026" \
        --copilot-fact PowerBI_Workload_Model_3_Scenarios_2026-08-07_0106.xlsx \
        --comments COMMENTS --employees employee_count.xlsx \
        --checklist "Critical_Projects_-__Workload_Forecast_Comments_Checklist.xlsx" --out ../output

Normal use once all workbooks are available (no Copilot file needed):
    python run_analysis.py --cycle "Q4 2026" --before "To Departments" --comments COMMENTS --after after ...
"""
from __future__ import annotations

import argparse
import datetime as dt
import re
from pathlib import Path

import openpyxl
import pandas as pd

from analysis import (DEPARTMENTS, checklist, copy_paste_check, department_summary, feedback_status, load_capacity,
                      monthly, possible_transfers, project_summary, suggest_codes)
from build_dataset import build
from compare import three_way
from powerbi_export import export_powerbi
from report_excel import write_workbook


def read_checklist(path) -> list[tuple[str, str]]:
    if not path or not Path(path).exists():
        return []
    ws = openpyxl.load_workbook(path, data_only=True).worksheets[0]
    out = []
    for r in range(4, ws.max_row + 1):
        code, title = ws.cell(r, 2).value, ws.cell(r, 3).value
        if code not in (None, "") and str(title).strip().lower() != "dummy":
            out.append((str(code).strip(), str(title).strip()))
    return out


def file_inventory(df: pd.DataFrame, fb: pd.DataFrame) -> pd.DataFrame:
    rec = []
    piping = {"2410", "2420", "2430", "2440", "2450"}
    for dept, name in DEPARTMENTS.items():
        for sc in ("Planning Input", "Discipline Comment", "System Implementation"):
            g = df[(df["Scenario"] == sc) & (df["DeptCode"] == dept)]
            file = g["SourceFile"].iloc[0] if len(g) else "(missing)"
            src = g["SourceType"].iloc[0] if len(g) else "-"
            action = ""
            if not len(g):
                action = "File missing - obtain it."
            elif sc == "Discipline Comment":
                status = fb.loc[fb["DeptCode"] == dept, "Feedback_Status"].iloc[0]
                if dept in piping:
                    action = ("Piping returned one consolidated file (2400-c.xlsx) which was not provided; the comment "
                              "folder holds an unchanged copy of the original. Upload 2400-c.xlsx to split into 2410-2450.")
                elif status.startswith("No comments"):
                    action = "Returned file is identical to the original - confirm 'no comments' with the department."
                elif src.startswith("Copilot"):
                    action = "Upload the -c workbook to resolve blank-vs-zero cells and red/yellow markings."
            elif src.startswith("Copilot"):
                action = "Original workbook not uploaded; values taken from the Copilot table after validation."
            rec.append({"Dept": int(dept), "Department": name, "Scenario": sc, "File Used": file,
                        "Source": src, "Action Needed": action})
    return pd.DataFrame(rec)


def _title_only(name) -> str:
    """Drop the leading project code from an activity title."""
    return re.sub(r"^[\d\s,\-]+", "", str(name or ""))


def build_findings(ds, ps, rows, issues, mdf, cap, note: str = "", possible=None) -> tuple[list[str], list[str]]:
    f = []
    P, F, I = ds["Planning_MH"].sum(), ds["Final_MH"].sum(), ds["Implemented_MH"].sum()
    inc, red = ds["Gross_Increase"].sum(), ds["Gross_Reduction"].sum()
    req = inc - red
    impl = (rows["Implemented_Request_MH"] if "Implemented_Request_MH" in rows else
            rows["Impl_Ratio"].fillna(0) * (rows["Gross_Increase"] - rows["Gross_Reduction"]))[rows["Concern"]].sum()
    unreq = ds["Unrequested_Gross"].sum()
    with_changes = ds[ds["Feedback_Status"] == "Feedback received - changes"]
    no_comments = ds[ds["Feedback_Status"].str.startswith("No comments")]
    f.append(f"Departments asked for a net {inc + red:+,.0f} MH ({(inc + red) / P:+.1%}) on a Planning Input of {P:,.0f} MH "
             f"(gross +{inc:,.0f} / {red:,.0f}). {len(with_changes)} of 26 departments changed figures; "
             f"{len(no_comments)} returned files identical to the original "
             f"({', '.join(no_comments['DeptCode'])})"
             + (", including the Piping departments, whose consolidated 2400-c.xlsx was not processed." if
                {"2410", "2420", "2430", "2440", "2450"} & set(no_comments["DeptCode"]) else "."))
    top = with_changes.assign(a=with_changes["Net_Deviation"].abs()).sort_values("a", ascending=False).head(4)
    f.append("Largest departmental deviations: " + "; ".join(
        f"{r.DeptCode} {r.Department} {r.Net_Deviation:+,.0f} MH ({r.Deviation_Pct:+.0%})" for r in top.itertuples()) + ".")
    pc = ds[ds["Concern_Items"] > 0].sort_values("Concern_Items", ascending=False).head(3)
    f.append("Most concern items: " + "; ".join(f"{r.DeptCode} {r.Department} ({int(r.Concern_Items)} items, "
                                               f"{int(r.Concern_Projects)} projects)" for r in pc.itertuples()) + ".")
    crit = ps[ps["Severity"] == "Critical"]
    broad = ps.sort_values(["Depts_Concerned", "Gross_Increase"], ascending=False).head(4)
    f.append(f"{len(crit)} projects rated Critical and {int((ps['Severity'] == 'High').sum())} High. Broadest concern: " + "; ".join(
        f"{r.ProjectFull} ({int(r.Depts_Concerned)} depts, {r.Net_Change:+,.0f} MH)" for r in broad.itertuples()) + ".")
    newp = rows[rows["Concern_Types"].str.contains("New Project|New Phase")]
    big_new = newp.groupby("ProjectFull").agg(MH=("Final_Total", "sum"), D=("DeptCode", "nunique"),
                                             Name=("D_Name", "first")).sort_values("MH", ascending=False).head(4)
    f.append(f"{len(newp)} new allocations ({newp['Final_Total'].sum():,.0f} MH) were requested; the largest are " + "; ".join(
        f"{k} {_title_only(v.Name)[:48]} ({v.MH:,.0f} MH, {int(v.D)} dept{'s' if v.D > 1 else ''})"
        for k, v in big_new.iterrows())
             + f". {int((~newp['I_Exists']).sum())} of them are absent from the after-implementation data.")
    f.append(f"Implementation: of {req:,.0f} MH of requested change, about {impl:,.0f} MH ({impl / req:.0%}) appears in the "
             f"after-implementation extract. Separately, the extract differs from Planning Input by {unreq:,.0f} MH (gross) "
             f"in cells where no department asked for a change, so most of the {I - P:+,.0f} MH movement is not "
             "explained by comments and should be reconciled before the extract is used as 'final'." + (f" {note}" if note else ""))
    ext = rows[rows["Concern_Types"].str.contains("Schedule Extension")]
    ext_top = ext.groupby("ProjectFull").agg(MH=("Extension_MH", "sum"), D=("DeptCode", "nunique")).sort_values("MH", ascending=False).head(4)
    f.append(f"{len(ext)} rows extend the schedule beyond the baseline finish ({ext['Extension_MH'].sum():,.0f} MH falls after "
             "the planned end). Largest: " + "; ".join(f"{k} ({v.MH:,.0f} MH, {int(v.D)} dept{'s' if v.D > 1 else ''})"
                                                     for k, v in ext_top.iterrows()) + ".")
    loan = rows[rows["Concern"] & (rows["Phase"] == "Loan To")]
    if len(loan):
        f.append(f"{len(loan)} Loan To rows were changed by {loan['DeptCode'].nunique()} departments (gross "
                 f"{(loan['Gross_Increase'] - loan['Gross_Reduction']).sum():,.0f} MH); Loan To is Corporate Planning controlled, "
                 "so these are requests, not edits.")
    if possible is not None and len(possible):
        top_pt = possible.sort_values("Possible Offset MH", ascending=False).iloc[0]
        f.append(f"{len(possible)} reductions look like transfers to a new code or phase in the same department "
                 f"({possible['Possible Offset MH'].sum():,.0f} MH would be counted twice in the gross figures), e.g. "
                 f"{top_pt['Dept']}: {top_pt['Reduced Project']} {top_pt['Reduced Phase']} {top_pt['Reduction MH']:,.0f} MH against new "
                 f"{top_pt['Added Project']} {top_pt['Added Phase']} +{top_pt['Added MH']:,.0f} MH.")
    moves = rows[rows["RequestedPhase"] != ""]
    if len(moves):
        f.append(f"{len(moves)} projects ({moves['P_Total'].sum():,.0f} MH) appear under a different phase in the feedback "
                 f"(departments {', '.join(sorted(moves['DeptCode'].unique(), key=int))}); these were paired, not added, "
                 "which prevents double counting. Category changes need Corporate Planning approval.")
    dup = sum(i["Impact_MH"] or 0 for i in issues if i["Area"] == "Double counting")
    hdr = sum(i["Impact_MH"] or 0 for i in issues if i["Issue"].startswith("Department / sub-department total rows"))
    nonmonth = sorted({i["File"] for i in issues if i["Issue"].startswith("Non-month column")})
    direct = sorted(rows.loc[rows["D_SourceType"].fillna("").eq("Source workbook"), "D_File"].dropna().unique())
    if hdr or dup or nonmonth:
        f.append("The Copilot 3-scenario model is not reliable as built: it counted department total rows as projects "
                 f"({hdr:,.0f} MH across scenarios)"
                 + (f", a non-month column in {', '.join(nonmonth)} as a month" if nonmonth else "")
                 + f", and after-implementation blocks that repeat other departments ({dup:,.0f} MH). "
                 + (f"Its project-level values were checked cell by cell against the {len(direct)} feedback files read "
                    "directly and reused after correction." if direct else "Its values were reused after correction."))
    util = ds.dropna(subset=["Firm_Utilisation_Final"]).sort_values("Firm_Utilisation_Final", ascending=False).head(5)
    dm = mdf.groupby(["DeptCode", "Month"])["Discipline_Final_MH"].sum().reset_index().merge(cap[["DeptCode", "Capacity_MH_per_Month"]], on="DeptCode")
    over = dm[dm["Discipline_Final_MH"] > dm["Capacity_MH_per_Month"]]
    f.append("Highest firm utilisation (Running + Internal + Loan To + Under Signature, discipline-adjusted): " + "; ".join(
        f"{r.DeptCode} {r.Firm_Utilisation_Final:.0%}" for r in util.itertuples()) +
             f". Including Proposal and Potential, {over['DeptCode'].nunique()} departments exceed capacity in at least one month "
             "- see 'Monthly Workload Impact'.")
    crit_codes = ps[ps["Severity"] == "Critical"].sort_values("Gross_Increase", ascending=False)
    new_crit = [c for c in crit_codes["ProjectFull"] if c in set(big_new.index)][:2]
    widest = ps.sort_values(["Depts_Concerned", "Gross_Increase"], ascending=False)["ProjectFull"].head(2).tolist()
    piping = {"2410", "2420", "2430", "2440", "2450"}
    silent = [d for d in no_comments["DeptCode"] if d not in piping]
    a = [
        "Corporate Planning to rule on the Critical projects first (see Project Ranking): accept, reduce or return each change with a "
        "reason, starting with " + (f"the new projects {', '.join(new_crit)} and " if new_crit else "")
        + f"the most widely raised projects {', '.join(widest)}.",
        "Planners to close every 'Not implemented' item in Implementation Tracking: implement it or mark it 'not applicable' with a reason, "
        "so the checklist no longer depends on email follow-up.",
        "Reconcile the after-implementation extract: explain the MH that changed without a discipline comment before the forecast is locked.",
    ]
    if len(no_comments):
        a.append(("Obtain the consolidated Piping feedback (2400-c.xlsx) and " if piping & set(no_comments["DeptCode"]) else "")
                 + (f"confirm 'no comments' from {', '.join(silent)}" if silent else "")
                 + ", so the analysis covers all 26 departments.")
    if len(moves):
        a.append(f"Confirm the phase/category changes requested by {', '.join(sorted(moves['DeptCode'].unique(), key=int))}; "
                 "Loan To stays Corporate Planning controlled.")
    a += ["Fix the template: replace =LEFT(B,7) with the full code, lock phase headings, and give departments a 'Reason' column - "
          "these three gaps caused most data-quality findings and are built into the web application design.",
          "Review hiring or redistribution for departments whose firm utilisation stays above 100% for several months."]
    return f, a


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--cycle", default="Forecast cycle")
    ap.add_argument("--copilot-fact")
    ap.add_argument("--before")
    ap.add_argument("--comments")
    ap.add_argument("--after")
    ap.add_argument("--employees", required=True)
    ap.add_argument("--checklist")
    ap.add_argument("--out", required=True)
    ap.add_argument("--note", default="", help="context sentence added to the implementation finding")
    a = ap.parse_args()
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    folders = {"Planning Input": a.before, "Discipline Comment": a.comments, "System Implementation": a.after}
    df, issues = build(a.copilot_fact, folders)
    cells, rows, transfers = three_way(df, issues)
    rows["Implemented_Request_MH"] = rows["Impl_Ratio"].fillna(0) * (rows["Gross_Increase"] - rows["Gross_Reduction"]) * rows["Concern"]
    issues += copy_paste_check(df)
    for s in suggest_codes(rows).itertuples():
        issues.append(dict(Severity="Medium", Area="Project code", Scenario="Discipline Comment", File=s.DeptCode,
                           Issue="Uncoded feedback row - closest known project (verify)",
                           Detail=f"'{s.Activity}' -> {s.Suggested_Code or 'no close match'} ({s.Match:.0%})", Impact_MH=s.Final_MH))
    possible = possible_transfers(rows)
    for _, t in possible.iterrows():
        issues.append(dict(Severity="High" if t["Possible Offset MH"] >= 5000 else "Medium", Area="Possible transfer",
                           Scenario="Discipline Comment", File=str(t["Dept"]),
                           Issue="Reduction paired with a new allocation of the same family or title (verify)",
                           Detail=f"{t['Reduced Project']} {t['Reduced Phase']} {t['Reduction MH']:,.0f} MH vs {t['Added Project']} "
                                  f"{t['Added Phase']} +{t['Added MH']:,.0f} MH ({t['Match Basis']})",
                           Impact_MH=float(t["Possible Offset MH"])))
    cap = load_capacity(a.employees)
    fb = feedback_status(rows, df)
    ds = department_summary(rows, cells, cap, fb)
    ps = project_summary(rows, cells)
    mdf = monthly(cells, rows)
    chk = checklist(rows, read_checklist(a.checklist))
    inv = file_inventory(df, fb)
    months = sorted(cells["Month"].unique())
    window = f"{months[0]:%b-%y} to {months[-1]:%b-%y} ({len(months)} months)"
    src = []
    if a.copilot_fact:
        src.append(f"Copilot table {Path(a.copilot_fact).name} (corrected, validated)")
    src += [f"{k}: folder '{Path(v).name}'" for k, v in folders.items() if v]
    meta = dict(cycle=a.cycle, window=window, generated=dt.date.today().strftime("%d-%b-%Y"), sources="; ".join(src),
                limitations="Original 'To Departments' and 'after' workbooks were not uploaded, so Planning Input and After "
                            "Implementation come from the validated Copilot table; 19 departments' feedback likewise. "
                            "Piping (2400-c.xlsx) feedback not provided. Headcount date unconfirmed.")
    findings, actions = build_findings(ds, ps, rows, issues, mdf, cap, a.note, possible)
    tag = a.cycle.replace(" ", "_")
    write_workbook(out / f"ENPPI_Workload_Comments_Analysis_{tag}.xlsx", rows=rows, cells=cells, transfers=transfers,
                   ds=ds, ps=ps, mdf=mdf, cap=cap, chk=chk, inventory=inv, issues=issues, findings=findings,
                   actions=actions, meta=meta, possible=possible)
    export_powerbi(out / "powerbi", df=df, cells=cells, rows=rows, ps=ps, cap=cap, issues=issues, cycle=a.cycle, possible=possible)
    pd.DataFrame(issues).to_csv(out / "issues_log.csv", index=False)
    print("\n".join(findings))
    print(f"\nWritten to {out}")


if __name__ == "__main__":
    main()
