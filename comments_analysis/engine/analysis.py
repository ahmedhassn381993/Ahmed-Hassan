"""Management aggregations: department, project, monthly, capacity, checks and checklist."""
from __future__ import annotations

import difflib
import math
from pathlib import Path

import numpy as np
import pandas as pd

from compare import KEY, TOL, _norm_name, severity

HOURS_PER_MAN_MONTH = 150          # as used in the Forecast Comments PBIX (AvailMHR = EffTotal*Months*150)
PERMANENT_EFFECTIVENESS = 0.9      # PBIX Dept query: Perm_EFF = ROUNDUP(Permanent_No * 0.9)
# PBIX measure "Projects Total%" = Running + Internal + Loan To + Under Signature (pipeline excluded)
FIRM_CATEGORIES = ["Running", "Internal / Unforeseen", "Loan To", "Under Signature"]
CATEGORY_ORDER = ["Running", "Internal / Unforeseen", "Loan To", "Under Signature", "Proposal", "Potential Opportunity"]

DEPARTMENTS = {
    "300": "Quality & Environmental Management System", "600": "Health, Safety & Environment",
    "1100": "Process Technology", "1180": "Pre-Commissioning & Commissioning",
    "2100": "Project Engineering Managers / Leaders", "2110": "Document Controllers",
    "2200": "Instruments", "2220": "Telecommunications", "2300": "Civil / Structural Engineering",
    "2410": "Piping Technical", "2420": "Piping Stress Analysis", "2430": "Piping Design",
    "2440": "Pipeline", "2450": "Underground", "2500": "Electrical Engineering",
    "2600": "Offshore Engineering", "2710": "Mechanical Engineering", "2720": "Pressure Vessels",
    "2800": "Buildings Engineering", "3100": "Project Managers / Engineers", "3300": "Cost Engineering",
    "3400": "Construction Supervision", "3500": "Planning", "4100": "Installations (E&I)",
    "5000": "Material Management", "5100": "Procurement",
}
# employee_count.xlsx uses 1190 for commissioning, 2700 for mechanical and 5110-5160 for procurement
HEADCOUNT_ALIAS = {"1180": ["1190"], "2710": ["2700"], "5100": ["5110", "5120", "5130", "5140", "5160"]}


def load_capacity(employee_xlsx: str | Path) -> pd.DataFrame:
    t = pd.read_excel(employee_xlsx, sheet_name="Table for Emails", header=0, usecols="A:F", nrows=31)
    t.columns = ["Code", "Managers", "LoanTo_Unpaid", "Permanent", "Temporary", "Total"]
    t = t[pd.to_numeric(t["Code"], errors="coerce").notna()].copy()
    t["Code"] = t["Code"].astype(int).astype(str)
    out = []
    for dept in DEPARTMENTS:
        codes = HEADCOUNT_ALIAS.get(dept, [dept])
        g = t[t["Code"].isin(codes)]
        if g.empty:
            out.append(dict(DeptCode=dept, HeadcountSource="not found"))
            continue
        s = g[["Managers", "LoanTo_Unpaid", "Permanent", "Temporary", "Total"]].sum()
        eff = math.ceil(s["Permanent"] * PERMANENT_EFFECTIVENESS) + s["Temporary"]
        out.append(dict(DeptCode=dept, HeadcountSource="+".join(codes), Managers=s["Managers"],
                        LoanTo_Unpaid=s["LoanTo_Unpaid"], Permanent=s["Permanent"], Temporary=s["Temporary"],
                        Total=s["Total"], Effective_Heads=eff, Capacity_MH_per_Month=eff * HOURS_PER_MAN_MONTH))
    return pd.DataFrame(out)


def feedback_status(rows: pd.DataFrame, df: pd.DataFrame) -> pd.DataFrame:
    """Was feedback received, and did it change anything?"""
    d = df[df["Scenario"] == "Discipline Comment"].groupby("DeptCode").agg(
        Feedback_File=("SourceFile", "first"), Feedback_Source=("SourceType", "first"))
    rec = []
    for dept in DEPARTMENTS:
        r = rows[rows["DeptCode"] == dept]
        f = d.loc[dept] if dept in d.index else None
        file = f["Feedback_File"] if f is not None else ""
        commented = file.endswith("-c.xlsx")
        status = ("No feedback file" if f is None else
                  "Feedback received - changes" if r["Concern"].any() else
                  "Feedback received - no MH change" if commented else
                  "No comments received (original returned / not processed)")
        rec.append(dict(DeptCode=dept, Department=DEPARTMENTS[dept], Feedback_File=file,
                        Feedback_Source=f["Feedback_Source"] if f is not None else "", Feedback_Status=status))
    return pd.DataFrame(rec)


def department_summary(rows: pd.DataFrame, cells: pd.DataFrame, cap: pd.DataFrame, fb: pd.DataFrame) -> pd.DataFrame:
    g = rows.groupby("DeptCode")
    s = g.agg(Planning_MH=("P_Total", "sum"), Final_MH=("Final_Total", "sum"), Implemented_MH=("I_Total", "sum"),
              Gross_Increase=("Gross_Increase", "sum"), Gross_Reduction=("Gross_Reduction", "sum"),
              Net_Deviation=("Net_Change", "sum"), Changed_Cells=("Changed_Cells", "sum"),
              Concern_Items=("Concern", "sum"), Extension_MH=("Extension_MH", "sum"),
              Ambiguous_Cells=("Ambiguous_Cells", "sum"), Ambiguous_MH=("Ambiguous_MH", "sum"),
              Unrequested_Gross=("Unrequested_Gross", "sum"), Unrequested_Net=("Unrequested_Net", "sum")).reset_index()
    cr = rows[rows["Concern"]]
    s = s.merge(cr.groupby("DeptCode")["ProjectFull"].nunique().rename("Concern_Projects").reset_index(), on="DeptCode", how="left")
    for name, pat in [("New_Projects", "New Project"), ("New_Phase_Allocations", "New Phase Allocation"),
                      ("Phase_Transfers", "Phase Transfer|Phase Label"), ("Schedule_Extensions", "Schedule Extension")]:
        s = s.merge(rows[rows["Concern_Types"].str.contains(pat)].groupby("DeptCode").size().rename(name).reset_index(),
                    on="DeptCode", how="left")
    w = cr.assign(W=cr["Impl_Ratio"].fillna(0) * (cr["Gross_Increase"] - cr["Gross_Reduction"]),
                  G=cr["Gross_Increase"] - cr["Gross_Reduction"]).groupby("DeptCode")[["W", "G"]].sum()
    s = s.merge((w["W"] / w["G"].replace(0, np.nan)).rename("Implementation_Rate").reset_index(), on="DeptCode", how="left")
    s = fb.merge(s, on="DeptCode", how="left").merge(cap, on="DeptCode", how="left")
    s = s.fillna({c: 0 for c in ["Concern_Projects", "New_Projects", "New_Phase_Allocations", "Phase_Transfers", "Schedule_Extensions"]})
    s["Deviation_Pct"] = s["Net_Deviation"] / s["Planning_MH"].replace(0, np.nan)
    months = cells["Month"].nunique()
    cat = cells.merge(rows[KEY + ["Category"]], on=KEY)
    firm = cat[cat["Category"].isin(FIRM_CATEGORIES)]
    for scen, col in [("Planning", "P0"), ("Final", "Final"), ("Implemented", "I0")]:
        peak = cells.groupby(["DeptCode", "Month"])[col].sum().groupby("DeptCode").max()
        s = s.merge(peak.rename(f"Peak_Month_MH_{scen}").reset_index(), on="DeptCode", how="left")
        s = s.merge(firm.groupby("DeptCode")[col].sum().rename(f"Firm_MH_{scen}").reset_index(), on="DeptCode", how="left")
        s[f"Avg_Utilisation_{scen}"] = s[f"{scen}_MH"] / months / s["Capacity_MH_per_Month"]
        s[f"Firm_Utilisation_{scen}"] = s[f"Firm_MH_{scen}"] / months / s["Capacity_MH_per_Month"]
        s[f"Peak_Utilisation_{scen}"] = s[f"Peak_Month_MH_{scen}"] / s["Capacity_MH_per_Month"]
    return s


def monthly(cells: pd.DataFrame, rows: pd.DataFrame) -> pd.DataFrame:
    """Department x category x month totals for the three scenarios (feeds charts and Power BI)."""
    m = cells.merge(rows[KEY + ["Category"]], on=KEY)
    return (m.groupby(["DeptCode", "Category", "Month"])[["P0", "Final", "I0"]].sum().reset_index()
            .rename(columns={"P0": "Planning_MH", "Final": "Discipline_Final_MH", "I0": "Implemented_MH"}))


def project_summary(rows: pd.DataFrame, cells: pd.DataFrame) -> pd.DataFrame:
    r = rows.copy()
    name = r["D_Name"].fillna(r["P_Name"]).fillna(r["I_Name"])
    r["Name"] = name
    g = r.groupby("ProjectFull")
    p = g.agg(Project_Base=("ProjectBase", "first"), Project_Name=("Name", lambda s: max(s, key=len)),
              P_Total=("P_Total", "sum"), Final_Total=("Final_Total", "sum"), I_Total=("I_Total", "sum"),
              Gross_Increase=("Gross_Increase", "sum"), Gross_Reduction=("Gross_Reduction", "sum"),
              Net_Change=("Net_Change", "sum"), Concern_Rows=("Concern", "sum"),
              Extension_MH=("Extension_MH", "sum")).reset_index()
    cr = r[r["Concern"]]
    depts = cr.groupby("ProjectFull")["DeptCode"].apply(lambda s: sorted(set(s), key=int))
    p = p.merge(depts.apply(len).rename("Depts_Concerned").reset_index(), on="ProjectFull", how="left")
    p = p.merge(depts.apply(lambda s: ", ".join(s)).rename("Departments_Raising").reset_index(), on="ProjectFull", how="left")
    p = p.merge(cr.groupby("ProjectFull")["Phase"].apply(lambda s: ", ".join(sorted(set(s)))).rename("Affected_Phases").reset_index(),
                on="ProjectFull", how="left")
    ch = cells[cells["Changed"]]
    # cells carry ProjectKey only; map back to the full code
    k2f = rows.set_index(KEY)["ProjectFull"]
    ch = ch.join(k2f, on=KEY)
    p = p.merge(ch.groupby("ProjectFull")["Month"].nunique().rename("Months_Affected").reset_index(), on="ProjectFull", how="left")
    span = ch.groupby("ProjectFull")["Month"].agg(["min", "max"])
    span = (span["min"].apply(lambda m: m.strftime("%b-%y")) + " to " + span["max"].apply(lambda m: m.strftime("%b-%y"))).rename("Affected_Months")
    p = p.merge(span.reset_index(), on="ProjectFull", how="left")
    types = cr.groupby("ProjectFull")["Concern_Types"].apply(lambda s: "; ".join(dict.fromkeys(t for x in s for t in x.split("; ") if t)))
    p = p.merge(types.rename("Concern_Types").reset_index(), on="ProjectFull", how="left")
    p["Has_New_Allocation"] = p["Concern_Types"].fillna("").str.contains("New Project|New Phase")
    p["Has_Extension"] = p["Concern_Types"].fillna("").str.contains("Schedule Extension")
    impl = cr.assign(W=cr["Impl_Ratio"].fillna(0) * (cr["Gross_Increase"] - cr["Gross_Reduction"]),
                     G=cr["Gross_Increase"] - cr["Gross_Reduction"]).groupby("ProjectFull")[["W", "G"]].sum()
    p = p.merge((impl["W"] / impl["G"].replace(0, np.nan)).rename("Implementation_Rate").reset_index(), on="ProjectFull", how="left")
    p = p.fillna({"Depts_Concerned": 0, "Months_Affected": 0, "Departments_Raising": "", "Affected_Phases": "",
                  "Affected_Months": "", "Concern_Types": ""})
    return severity(p).sort_values(["Severity_Points", "Gross_Increase"], ascending=False)


def copy_paste_check(df: pd.DataFrame) -> list[dict]:
    """Feedback rows whose whole monthly profile repeats another row of the same file."""
    out = []
    d = df[(df["Scenario"] == "Discipline Comment") & df["SourceFile"].str.contains("-c", regex=False)]
    for fn, g in d.groupby("SourceFile"):
        vec = g.pivot_table(index=["SourceRow", "ProjectFull", "ActivityName"], columns="Month", values="MH", aggfunc="sum").fillna(0)
        vec = vec[vec.apply(lambda r: r[r > 0].nunique() >= 3, axis=1)]   # varied profile, not a flat rate
        vec = vec[vec.sum(axis=1) >= 5000]
        sig = vec.apply(lambda r: tuple(r.round(1)), axis=1)
        for s, grp in vec.groupby(sig):
            if len(grp) > 1 and grp.index.get_level_values(1).nunique() > 1:
                desc = "; ".join(f"row {i[0]} {i[1]}" for i in grp.index)
                out.append(dict(Severity="Medium", Area="Possible copy-paste", Scenario="Discipline Comment", File=fn,
                                Issue="Different projects with identical 24-month profile",
                                Detail=desc, Impact_MH=float(grp.sum(axis=1).iloc[0])))
    return out


def suggest_codes(rows: pd.DataFrame) -> pd.DataFrame:
    """For uncoded feedback rows, propose the closest coded project title in the dataset."""
    known = rows[rows["ProjectFull"] != ""].copy()
    known["N"] = known["D_Name"].fillna(known["P_Name"]).fillna(known["I_Name"]).map(_norm_name)
    lib = known.drop_duplicates("ProjectFull")[["ProjectFull", "N"]]
    rec = []
    for r in rows[rows["ProjectFull"] == ""].itertuples():
        n = _norm_name(r.D_Name if isinstance(r.D_Name, str) else r.P_Name)
        best = max(((difflib.SequenceMatcher(None, n, x).ratio(), c) for c, x in zip(lib["ProjectFull"], lib["N"])), default=(0, ""))
        rec.append(dict(DeptCode=r.DeptCode, Phase=r.Phase, Activity=r.D_Name, Final_MH=r.Final_Total,
                        Suggested_Code=best[1] if best[0] >= 0.55 else "", Match=best[0]))
    return pd.DataFrame(rec)


CHECKLIST_DEPTS = ["300", "600", "1100", "1180", "2100", "2110", "2200", "2220", "2300", "2400", "2500", "2600",
                   "2710", "2720", "2800", "3100", "3300", "3400", "3500", "4100", "5000", "5100"]


def checklist(rows: pd.DataFrame, critical: list[tuple[str, str]]) -> pd.DataFrame:
    """Auto-fill the 'Critical Projects - Workload Forecast Comments Checklist' statuses."""
    r = rows.copy()
    r["Dept4"] = r["DeptCode"].where(~r["DeptCode"].isin(["2410", "2420", "2430", "2440", "2450"]), "2400")
    out = []
    for code, title in critical:
        rec = {"Project No.": code, "Project Title": title}
        sub = r[r["ProjectBase"] == str(code).split("-")[0]] if "-" not in str(code) else r[r["ProjectFull"] == str(code)]
        for d in CHECKLIST_DEPTS:
            x = sub[(sub["Dept4"] == d) & sub["Concern"]]
            if x.empty:
                rec[d] = "No discipline comments"
                continue
            ratio = (x["Impl_Ratio"].fillna(0) * (x["Gross_Increase"] - x["Gross_Reduction"])).sum() / max(
                (x["Gross_Increase"] - x["Gross_Reduction"]).sum(), 1e-9)
            rec[d] = ("Comments received & implemented" if ratio >= 0.95 else
                      "Comments received & partially implemented" if ratio >= 0.05 else
                      "Comments received - not implemented (planner to confirm if not applicable)")
        out.append(rec)
    return pd.DataFrame(out)
