"""Management aggregations: department, project, monthly, capacity, checks and checklist."""
from __future__ import annotations

import datetime as dt
import difflib
import math
import re
from pathlib import Path

import openpyxl

import numpy as np
import pandas as pd

from compare import KEY, TOL, _norm_name, _sim, severity
from workbook_parser import INTERNAL_CODE

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


def load_tracker(path: str | Path | None) -> dict[str, dict]:
    """Sent / received dates per department from the round tracker ('Disciplines Review' sheet)."""
    if not path or not Path(path).exists():
        return {}
    wb = openpyxl.load_workbook(path, data_only=True)
    if "Disciplines Review" not in wb.sheetnames:
        return {}
    ws = wb["Disciplines Review"]
    out = {}
    for r in ws.iter_rows(min_row=2, values_only=True):
        m = re.match(r"^\s*(\d{3,4})", str(r[0] or ""))
        if not m:
            continue
        date = lambda v: v.date() if isinstance(v, dt.datetime) else None
        rec = {"Sent": date(r[1]), "Received": date(r[2]), "Label": str(r[0]).strip()}
        code = m.group(1)
        for d in (["2410", "2420", "2430", "2440", "2450"] if code == "2400" else [code]):
            out[d] = rec
    return out


def feedback_status(rows: pd.DataFrame, df: pd.DataFrame, tracker: dict | None = None) -> pd.DataFrame:
    """Was feedback received, was the file available for analysis, and did it change anything?"""
    tracker = tracker or {}
    d = df[df["Scenario"] == "Discipline Comment"].groupby("DeptCode").agg(
        Feedback_File=("SourceFile", "first"), Feedback_Source=("SourceType", "first"))
    rec = []
    for dept in DEPARTMENTS:
        r = rows[rows["DeptCode"] == dept]
        f = d.loc[dept] if dept in d.index else None
        file = f["Feedback_File"] if f is not None else ""
        commented = file.endswith("-c.xlsx")
        tr = tracker.get(dept, {})
        if commented:
            status = "Feedback received - changes" if r["Concern"].any() else "Feedback received - no MH change"
        elif tr.get("Received"):
            status = f"Comments received {tr['Received']:%d-%b-%y} (tracker) - file not provided"
        elif tr:
            status = "No comments received (tracker)"
        elif file:
            status = "Original returned - no comment file"
        else:
            status = "No feedback file"
        rec.append(dict(DeptCode=dept, Department=DEPARTMENTS[dept], Feedback_File=file,
                        Feedback_Source=f["Feedback_Source"] if f is not None else "", Feedback_Status=status,
                        Tracker_Sent=tr.get("Sent"), Tracker_Received=tr.get("Received"),
                        Feedback_Processed=commented))
    return pd.DataFrame(rec)


def department_summary(rows: pd.DataFrame, cells: pd.DataFrame, cap: pd.DataFrame, fb: pd.DataFrame) -> pd.DataFrame:
    g = rows.groupby("DeptCode")
    s = g.agg(Planning_MH=("P_Total", "sum"), Final_MH=("Final_Total", "sum"), Implemented_MH=("I_Total", "sum"),
              Gross_Increase=("Gross_Increase", "sum"), Gross_Reduction=("Gross_Reduction", "sum"),
              Net_Deviation=("Net_Change", "sum"), Changed_Cells=("Changed_Cells", "sum"),
              Concern_Items=("Concern", "sum"), Extension_MH=("Extension_MH", "sum"),
              Ambiguous_Cells=("Ambiguous_Cells", "sum"), Ambiguous_MH=("Ambiguous_MH", "sum"),
              Unrequested_Gross=("Unrequested_Gross", "sum"), Unrequested_Net=("Unrequested_Net", "sum"),
              BlankRetained_MH=("BlankRetained_MH", "sum"), Bridge_New_Work=("Bridge_New_Work", "sum"),
              Bridge_Increase=("Bridge_Increase", "sum"), Bridge_Extension=("Bridge_Extension", "sum"),
              Bridge_Reduction=("Bridge_Reduction", "sum"), Bridge_Removed=("Bridge_Removed", "sum")).reset_index()
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
    capm = cap.set_index("DeptCode")["Capacity_MH_per_Month"]
    for label, src in (("Overload_Months_Final", cat), ("Firm_Overload_Months_Final", firm)):
        mm = src.groupby(["DeptCode", "Month"])["Final"].sum().reset_index()
        mm = mm[mm["Final"] > mm["DeptCode"].map(capm)]
        s = s.merge(mm.groupby("DeptCode").size().rename(label).reset_index(), on="DeptCode", how="left")
        s[label] = s[label].fillna(0).astype(int)
    return s


def company_peaks(mdf: pd.DataFrame, cap: pd.DataFrame) -> dict:
    """Peak month and utilisation of the whole company per scenario."""
    total_cap = cap["Capacity_MH_per_Month"].sum()
    m = mdf.groupby("Month")[["Planning_MH", "Discipline_Final_MH", "Implemented_MH"]].sum()
    out = {}
    for col in m.columns:
        mon = m[col].idxmax()
        out[col] = {"Month": mon, "MH": float(m[col].max()), "Utilisation": float(m[col].max() / total_cap),
                    "Months_Over": int((m[col] > total_cap).sum())}
    return out


def section_breakdown(rows: pd.DataFrame) -> pd.DataFrame:
    """Sub-department totals for departments that hold several sections (5100: 5110-5160)."""
    multi = rows.groupby("DeptCode")["SectionCode"].nunique()
    r = rows[rows["DeptCode"].isin(multi[multi > 1].index) & (rows["P_Exists"] | rows["D_Exists"])]
    g = r.groupby(["DeptCode", "SectionCode"]).agg(
        Planning_MH=("P_Total", "sum"), Discipline_MH=("Final_Total", "sum"), Implemented_MH=("I_Total", "sum"),
        Gross_Increase=("Gross_Increase", "sum"), Gross_Reduction=("Gross_Reduction", "sum"),
        Net_Change=("Net_Change", "sum"), Concern_Items=("Concern", "sum")).reset_index()
    return g


def load_master(path: str | Path | None) -> pd.DataFrame:
    """ENPPI project master (PROJECTS_my_db.xlsx): code -> name, client, category."""
    if not path or not Path(path).exists():
        return pd.DataFrame(columns=["Code", "Master_Name", "Master_Client", "Master_Category"])
    m = pd.read_excel(path, usecols=["PROJECT_NO", "PROJECT_NAME", "CLIENT_SHORT_NAME", "PROJECT_CATEGORY"],
                      dtype={"PROJECT_NO": str})
    m["Code"] = m["PROJECT_NO"].astype(str).str.replace(r"\.0$", "", regex=True).str.strip()
    return (m.rename(columns={"PROJECT_NAME": "Master_Name", "CLIENT_SHORT_NAME": "Master_Client",
                              "PROJECT_CATEGORY": "Master_Category"})
            [["Code", "Master_Name", "Master_Client", "Master_Category"]].drop_duplicates("Code"))


def attach_master(df: pd.DataFrame, code_col: str, master: pd.DataFrame) -> pd.DataFrame:
    """Add master name/client and a flag: not in master, internal/overhead account, or no code."""
    out = df.copy()
    base = out[code_col].astype(str).str.split("-").str[0]
    out = out.merge(master.rename(columns={"Code": "_base"}), left_on=base, right_on="_base", how="left").drop(columns="_base")
    if "key_0" in out.columns:
        out = out.drop(columns="key_0")
    flag = np.select(
        [out[code_col].astype(str).str.startswith("UNCODED"),
         base.values == INTERNAL_CODE,
         out["Master_Name"].isna(),
         out["Master_Category"].isin(["I", "O"]) | out["Master_Name"].fillna("").str.contains("DEVELOPMENT AND CUSTOMER", case=False)],
        ["No project code", "Internal / unforeseen workload code", "Not in project master (new or proposal code)",
         "Internal / overhead account used for project work"], "")
    out["Master_Flag"] = flag
    return out


def monthly(cells: pd.DataFrame, rows: pd.DataFrame) -> pd.DataFrame:
    """Department x category x month totals for the three scenarios (feeds charts and Power BI)."""
    m = cells.merge(rows[KEY + ["Category"]], on=KEY)
    return (m.groupby(["DeptCode", "Category", "Month"])[["P0", "Final", "I0"]].sum().reset_index()
            .rename(columns={"P0": "Planning_MH", "Final": "Discipline_Final_MH", "I0": "Implemented_MH"}))


def project_summary(rows: pd.DataFrame, cells: pd.DataFrame, master: pd.DataFrame | None = None) -> pd.DataFrame:
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
    variants = r.groupby("ProjectFull")["Name"].apply(
        lambda s: " | ".join(dict.fromkeys(re.sub(r"^[\d\s,\-]+", "", str(x)).strip()[:45] for x in s.dropna()))
        if s.dropna().map(_norm_name).map(lambda n: n[:25]).nunique() > 1 else "")
    p = p.merge(variants.rename("Title_Variants").reset_index(), on="ProjectFull", how="left")
    if master is not None:
        p = attach_master(p, "ProjectFull", master)
    p = severity(p)
    p["Gross_Change"] = p["Gross_Increase"] - p["Gross_Reduction"]
    return p.sort_values(["Severity_Points", "Gross_Change", "Gross_Increase"], ascending=False)


def copy_paste_check(df: pd.DataFrame) -> list[dict]:
    """Feedback rows of different projects whose sequence of monthly values is the same, even when one is
    shifted in time (1100-c: 5482100 and 8700100 repeat the tail of 5477100 from Mar-27)."""
    out = []
    d = df[(df["Scenario"] == "Discipline Comment") & df["SourceFile"].str.contains("-c", regex=False)]
    for fn, g in d.groupby("SourceFile"):
        vec = g.pivot_table(index=["SourceRow", "ProjectFull"], columns="Month", values="MH", aggfunc="sum").fillna(0)
        nz = vec.apply(lambda r: tuple(r[r > 0].round(1)), axis=1)
        keep = nz.map(len) >= 5
        keep &= vec.sum(axis=1) >= 5000
        keep &= nz.map(lambda t: len(set(t)) >= 3)         # varied profile, not a flat monthly rate
        sig = nz[keep]
        tails = {}
        for idx, t in sig.items():
            for k in range(0, len(t) - 4):                  # a row may repeat the tail of a longer row
                tails.setdefault(t[k:], []).append(idx)
        for idx, t in sig.items():
            same = [j for j in tails.get(t, []) if j != idx and j[1] != idx[1]]
            if same and idx[0] < min(j[0] for j in same + [idx]) + 10_000:
                rows_ = sorted({idx, *same})
                if rows_[0] != idx and idx not in [x for x in rows_[:1]]:
                    pass
                key = tuple(rows_)
                if any(o.get("_key") == key for o in out):
                    continue
                out.append(dict(Severity="Medium", Area="Possible copy-paste", Scenario="Discipline Comment", File=fn,
                                Issue="Different projects share the same monthly profile (possibly shifted) - confirm with the department",
                                Detail="; ".join(f"row {r_} {c_}" for r_, c_ in rows_), Impact_MH=float(sum(t)), _key=key))
    for o in out:
        o.pop("_key", None)
    return out


def suggest_codes(rows: pd.DataFrame) -> pd.DataFrame:
    """For uncoded feedback rows, propose the closest coded project title in the dataset."""
    uncoded = rows["ProjectFull"].str.startswith("UNCODED")
    known = rows[~uncoded].copy()
    known["N"] = known["D_Name"].fillna(known["P_Name"]).fillna(known["I_Name"]).map(_norm_name)
    lib = known.drop_duplicates("ProjectFull")[["ProjectFull", "N"]]
    rec = []
    for r in rows[uncoded].itertuples():
        n = _norm_name(r.D_Name if isinstance(r.D_Name, str) else r.P_Name)
        best = max(((difflib.SequenceMatcher(None, n, x).ratio(), c) for c, x in zip(lib["ProjectFull"], lib["N"])), default=(0, ""))
        rec.append(dict(DeptCode=r.DeptCode, Phase=r.Phase, Activity=r.D_Name, Final_MH=r.Final_Total,
                        Suggested_Code=best[1] if best[0] >= 0.55 else "", Match=best[0]))
    return pd.DataFrame(rec)


CHECKLIST_DEPTS = ["300", "600", "1100", "1180", "2100", "2110", "2200", "2220", "2300", "2400", "2500", "2600",
                   "2710", "2720", "2800", "3100", "3300", "3400", "3500", "4100", "5000", "5100"]


def checklist(rows: pd.DataFrame, critical: list[dict], fb: pd.DataFrame | None = None) -> pd.DataFrame:
    """Auto-fill the 'Critical Projects - Workload Forecast Comments Checklist' statuses."""
    r = rows.copy()
    piping = ["2410", "2420", "2430", "2440", "2450"]
    r["Dept4"] = r["DeptCode"].where(~r["DeptCode"].isin(piping), "2400")
    fbs = {} if fb is None else dict(zip(fb["DeptCode"], fb["Feedback_Status"]))
    fbs["2400"] = fbs.get("2410", "")
    out = []
    for c in critical:
        code = str(c["code"])
        rec = {"Project No.": code, "Project Title": c["title"], "Client": c.get("client", ""),
               "Planning Engineer": c.get("engineer", ""), "Planning Supervisor": c.get("supervisor", "")}
        sub = r[r["ProjectBase"] == code.split("-")[0]] if "-" not in code else r[r["ProjectFull"] == code]
        for d in CHECKLIST_DEPTS:
            st = fbs.get(d, "")
            if st.startswith("Comments received") and "file not provided" in st:
                rec[d] = "Comments received - file not provided"
                continue
            if st.startswith(("No comments received", "No feedback file")):
                rec[d] = "Feedback not received"
                continue
            x = sub[(sub["Dept4"] == d) & sub["Concern"]]
            if x.empty:
                rec[d] = "No discipline comments"
                continue
            gross = (x["Gross_Increase"] - x["Gross_Reduction"]).sum()
            if gross < TOL:
                rec[d] = "Comments received - code / phase change (confirm in Primavera)"
                continue
            ratio = (x["Impl_Ratio"].fillna(0) * (x["Gross_Increase"] - x["Gross_Reduction"])).sum() / gross
            rec[d] = ("Comments received & implemented" if ratio >= 0.95 else
                      "Comments received & partially implemented" if ratio >= 0.05 else
                      "Comments received - not implemented (planner to confirm if not applicable)")
        out.append(rec)
    return pd.DataFrame(out)


def possible_transfers(rows: pd.DataFrame) -> pd.DataFrame:
    """Within one department, a reduction or removal paired with a NEW allocation of the same project
    family (first five digits, e.g. 52482xx) or a similar title: the same work may have been re-coded or
    moved to another phase, so the gross figures double count it. Reported for review, never netted."""
    r = rows[rows["Concern"]].copy()
    r["Name"] = r["D_Name"].fillna(r["P_Name"]).fillna("")
    out = []
    for dept, g in r.groupby("DeptCode"):
        downs = g[g["Net_Change"] <= -TOL]
        ups = g[(g["Net_Change"] >= TOL) & g["Primary_Type"].str.startswith("New")]
        used = set()
        for d in downs.sort_values("Net_Change").itertuples():
            best = None
            for u in ups.itertuples():
                if u.Index in used or u.ProjectFull == d.ProjectFull:
                    continue
                fam = len(d.ProjectBase) >= 7 and d.ProjectBase[:5] == u.ProjectBase[:5]
                sim = _sim(d.Name, u.Name)
                score = (1 if fam else 0) + sim
                if (fam or sim >= 0.6) and (best is None or score > best[0]):
                    best = (score, u, fam, sim)
            if best is None:
                continue
            _, u, fam, sim = best
            used.add(u.Index)
            offset = min(-d.Net_Change, u.Net_Change)
            out.append({"Dept": int(dept), "Reduced Project": d.ProjectFull, "Reduced Phase": d.Phase, "Reduced Title": d.Name,
                        "Reduction MH": d.Net_Change, "Added Project": u.ProjectFull, "Added Phase": u.Phase,
                        "Added Title": u.Name, "Added MH": u.Net_Change,
                        "Match Basis": (f"same project family {d.ProjectBase[:5]}xx" if fam else "") +
                                       ("; " if fam and sim >= 0.6 else "") + (f"title {sim:.0%} similar" if sim >= 0.6 else ""),
                        "Possible Offset MH": offset, "Net of the Pair MH": d.Net_Change + u.Net_Change})
    return pd.DataFrame(out)
