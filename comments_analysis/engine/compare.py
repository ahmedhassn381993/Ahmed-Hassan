"""Three-way comparison: Planning Input (P) vs Discipline Comment (D) vs System Implementation (I).

Business rules (controlling brief):
  * Feedback blank            -> Final = P, deviation 0, no concern (blank is never zero)
  * Feedback equals P         -> Final = P, deviation 0, no concern
  * Feedback populated, != P  -> Final = D, deviation = D - P, concern
  * Rows only in feedback     -> new allocation (New Project / New Phase Allocation)
  * Rows missing in feedback  -> retained, NOT treated as removed
  * Reduction in one phase + increase in another for the same project -> Phase Transfer,
    reported as gross increase, gross reduction, net and the offsetting redistribution.
Match key: Department + Sub-department + Phase + full Project code (+ occurrence) + Month.
"""
from __future__ import annotations

import difflib
import re

import numpy as np
import pandas as pd

KEY = ["DeptCode", "SectionCode", "Phase", "ProjectKey"]
TOL = 0.5  # man-hours


def _norm_name(s: str) -> str:
    s = str(s or "").lower()
    s = re.sub(r"^\s*[\d\s\-_,.]+", "", s)          # leading project code tokens
    s = re.sub(r"[^a-z0-9 ]+", " ", s)
    return re.sub(r"\s+", " ", s).strip()


def _sim(a: str, b: str) -> float:
    a, b = _norm_name(a), _norm_name(b)
    if not a or not b:
        return 0.0
    return difflib.SequenceMatcher(None, a, b).ratio()


def add_keys(df: pd.DataFrame, issues: list[dict]) -> pd.DataFrame:
    """Occurrence index separates two rows that carry the same code in the same phase."""
    df = df.copy()
    rows = df.drop_duplicates(["Scenario", "DeptCode", "SectionCode", "Phase", "ProjectFull", "SourceRow"])
    rows = rows.sort_values("SourceRow")
    rows["Occ"] = rows.groupby(["Scenario", "DeptCode", "SectionCode", "Phase", "ProjectFull"]).cumcount() + 1
    dups = rows[(rows["Occ"] > 1) & (rows["ProjectFull"] != "")]
    for _, r in dups.iterrows():
        first = rows[(rows["Scenario"] == r.Scenario) & (rows["DeptCode"] == r.DeptCode) &
                     (rows["SectionCode"] == r.SectionCode) & (rows["Phase"] == r.Phase) &
                     (rows["ProjectFull"] == r.ProjectFull) & (rows["Occ"] == 1)].iloc[0]
        issues.append(dict(Severity="Medium", Area="Duplicate project row", Scenario=r.Scenario,
                           File=r.SourceFile, Issue="Same project code appears twice in one phase",
                           Detail=f"{r.ProjectFull} rows {first.SourceRow} & {r.SourceRow}: "
                                  f"'{first.ActivityName[:45]}' / '{r.ActivityName[:45]}'", Impact_MH=None))
    df = df.merge(rows[["Scenario", "SourceFile", "SourceRow", "Occ"]], on=["Scenario", "SourceFile", "SourceRow"], how="left")
    df["ProjectKey"] = np.where(df["Occ"] > 1, df["ProjectFull"] + "#" + df["Occ"].astype(str), df["ProjectFull"])
    df.loc[df["ProjectFull"] == "", "ProjectKey"] = "UNCODED:" + df["ActivityName"].str[:40]
    return df


def _pair_code_corrections(df: pd.DataFrame, issues: list[dict]) -> pd.DataFrame:
    """A feedback row with no baseline match + a baseline row missing from feedback, same department,
    sub-department and phase, near-identical title -> the department corrected the project code."""
    df = df.copy()
    rows = df.drop_duplicates(["Scenario"] + KEY)[["Scenario"] + KEY + ["ActivityName", "ProjectFull"]]
    p = rows[rows["Scenario"] == "Planning Input"]
    d = rows[rows["Scenario"] == "Discipline Comment"]
    pk, dk = set(map(tuple, p[KEY].values)), set(map(tuple, d[KEY].values))
    new_d = d[[tuple(x) not in pk for x in d[KEY].values]]
    miss_p = p[[tuple(x) not in dk for x in p[KEY].values]]
    df["CodeCorrection"] = ""
    for _, nd in new_d.iterrows():
        cands = miss_p[(miss_p["DeptCode"] == nd.DeptCode) & (miss_p["SectionCode"] == nd.SectionCode) &
                       (miss_p["Phase"] == nd.Phase)]
        if cands.empty:
            continue
        scores = cands["ActivityName"].apply(lambda n: _sim(n, nd.ActivityName))
        if scores.max() < 0.80:
            continue
        mp = cands.loc[scores.idxmax()]
        miss_p = miss_p.drop(scores.idxmax())
        for sc in ("Planning Input", "System Implementation"):
            sel = ((df["Scenario"] == sc) & (df["DeptCode"] == mp.DeptCode) & (df["SectionCode"] == mp.SectionCode)
                   & (df["Phase"] == mp.Phase) & (df["ProjectKey"] == mp.ProjectKey))
            has_new = ((df["Scenario"] == sc) & (df["DeptCode"] == nd.DeptCode) & (df["SectionCode"] == nd.SectionCode)
                       & (df["Phase"] == nd.Phase) & (df["ProjectKey"] == nd.ProjectKey)).any()
            if not has_new:
                df.loc[sel, "ProjectKey"] = nd.ProjectKey
        df.loc[(df["DeptCode"] == nd.DeptCode) & (df["SectionCode"] == nd.SectionCode) & (df["Phase"] == nd.Phase)
               & (df["ProjectKey"] == nd.ProjectKey), "CodeCorrection"] = f"{mp.ProjectFull} -> {nd.ProjectFull}"
        issues.append(dict(Severity="Low", Area="Project code", Scenario="Discipline Comment",
                           File=f"{nd.DeptCode}", Issue="Project code corrected by department (paired by title)",
                           Detail=f"{mp.ProjectFull} -> {nd.ProjectFull} ({scores.max():.0%} title match): {nd.ActivityName[:60]}",
                           Impact_MH=None))
    return df


def _pair_phase_moves(df: pd.DataFrame, issues: list[dict]) -> pd.DataFrame:
    """Same project in a different phase in the feedback (and missing from its baseline phase) is a
    requested phase transfer, not new work. The feedback row is re-keyed to the baseline phase so the
    MH comparison is like-for-like; the category change is reported for Corporate Planning approval."""
    df = df.copy()
    df["RequestedPhase"] = ""
    rows = df.drop_duplicates(["Scenario"] + KEY)[["Scenario"] + KEY + ["ProjectFull"]]
    p, d = rows[rows["Scenario"] == "Planning Input"], rows[rows["Scenario"] == "Discipline Comment"]
    pk, dk = set(map(tuple, p[KEY].values)), set(map(tuple, d[KEY].values))
    new_d = d[[tuple(x) not in pk for x in d[KEY].values]]
    miss_p = p[[tuple(x) not in dk for x in p[KEY].values]]
    moved_by_file: dict[str, list[str]] = {}
    for _, nd in new_d.iterrows():
        m = miss_p[(miss_p["DeptCode"] == nd.DeptCode) & (miss_p["SectionCode"] == nd.SectionCode) &
                   (miss_p["ProjectKey"] == nd.ProjectKey) & (miss_p["Phase"] != nd.Phase)]
        if m.empty:
            continue
        mp = m.iloc[0]
        miss_p = miss_p.drop(m.index[0])
        sel = ((df["Scenario"] == "Discipline Comment") & (df["DeptCode"] == nd.DeptCode) &
               (df["SectionCode"] == nd.SectionCode) & (df["Phase"] == nd.Phase) & (df["ProjectKey"] == nd.ProjectKey))
        df.loc[sel, "RequestedPhase"] = nd.Phase
        df.loc[sel, "Phase"] = mp.Phase
        moved_by_file.setdefault(nd.DeptCode, []).append(f"{nd.ProjectKey}: {mp.Phase} -> {nd.Phase}")
    for dept, moves in moved_by_file.items():
        issues.append(dict(Severity="High" if len(moves) >= 3 else "Medium", Area="Phase transfer",
                           Scenario="Discipline Comment", File=dept,
                           Issue="Projects found under a different phase in feedback (paired, baseline phase kept)",
                           Detail=f"{len(moves)} rows: " + "; ".join(moves[:12]) + (" ..." if len(moves) > 12 else ""),
                           Impact_MH=None))
    # the after-implementation extract can lose phase headings (after/2600.xlsx has none), so pair its
    # rows with the planning row of the same code in another phase in the same way
    rows = df.drop_duplicates(["Scenario"] + KEY)[["Scenario"] + KEY]
    p, i = rows[rows["Scenario"] == "Planning Input"], rows[rows["Scenario"] == "System Implementation"]
    known = set(map(tuple, rows[rows["Scenario"] != "System Implementation"][KEY].values))
    ik = set(map(tuple, i[KEY].values))
    orphan_i = i[[tuple(x) not in known for x in i[KEY].values]]
    free_p = p[[tuple(x) not in ik for x in p[KEY].values]]
    # phases that still have rows in each after file: a lost heading merges its whole block into the phase above,
    # so the planning phase is then empty in the after file; a phase that still has rows means the planner
    # deliberately filed the project elsewhere, which is reported as a category change, not re-keyed
    i_phases = set(zip(i["DeptCode"], i["SectionCode"], i["Phase"]))
    paired: dict[str, list[str]] = {}
    moved: dict[str, list[str]] = {}
    for _, ni in orphan_i.iterrows():
        m = free_p[(free_p["DeptCode"] == ni.DeptCode) & (free_p["SectionCode"] == ni.SectionCode) &
                   (free_p["ProjectKey"] == ni.ProjectKey) & (free_p["Phase"] != ni.Phase)]
        if m.empty:
            continue
        mp = m.iloc[0]
        if (ni.DeptCode, ni.SectionCode, mp.Phase) in i_phases:
            moved.setdefault(ni.DeptCode, []).append(f"{ni.ProjectKey}: {mp.Phase} -> {ni.Phase}")
            continue
        free_p = free_p.drop(m.index[0])
        sel = ((df["Scenario"] == "System Implementation") & (df["DeptCode"] == ni.DeptCode) &
               (df["SectionCode"] == ni.SectionCode) & (df["Phase"] == ni.Phase) & (df["ProjectKey"] == ni.ProjectKey))
        df.loc[sel, "Phase"] = mp.Phase
        paired.setdefault(ni.DeptCode, []).append(f"{ni.ProjectKey}: {ni.Phase} -> {mp.Phase}")
    for dept, moves in paired.items():
        issues.append(dict(Severity="Medium", Area="After-implementation data", Scenario="System Implementation", File=dept,
                           Issue="After rows filed under another phase (missing phase headings) - paired to planning by code",
                           Detail="; ".join(moves), Impact_MH=None))
    for dept, moves in moved.items():
        issues.append(dict(Severity="Medium", Area="After-implementation data", Scenario="System Implementation", File=dept,
                           Issue="Planner filed a project under another category after implementation (not paired - confirm)",
                           Detail="; ".join(moves), Impact_MH=None))
    return df


def three_way(df: pd.DataFrame, issues: list[dict]) -> tuple[pd.DataFrame, pd.DataFrame, pd.DataFrame]:
    """Return (cells, rows, transfers): cell-level P/D/Final/I, row classification, phase transfers."""
    df = _pair_code_corrections(_pair_phase_moves(add_keys(df, issues), issues), issues)
    months = sorted(df["Month"].unique())
    attrs = (df.sort_values("ScenarioOrder").groupby(KEY)
             .agg(Category=("Category", "first"), ProjectFull=("ProjectFull", "last"),
                  ProjectBase=("ProjectBase", "last"), ProjectSuffix=("ProjectSuffix", "last"),
                  CodeCorrection=("CodeCorrection", "max"), RequestedPhase=("RequestedPhase", "max")).reset_index())
    names = df.drop_duplicates(["Scenario"] + KEY).pivot_table(index=KEY, columns="Scenario", values="ActivityName", aggfunc="first")
    names = names.rename(columns={"Planning Input": "P_Name", "Discipline Comment": "D_Name", "System Implementation": "I_Name"})
    srcrow = df.drop_duplicates(["Scenario"] + KEY).pivot_table(index=KEY, columns="Scenario", values="SourceRow", aggfunc="first")
    srcrow = srcrow.rename(columns={"Planning Input": "P_Row", "Discipline Comment": "D_Row", "System Implementation": "I_Row"})
    dmeta = (df[df["Scenario"] == "Discipline Comment"].groupby(KEY)
             .agg(D_File=("SourceFile", "first"), D_SourceType=("SourceType", "first"), D_RowFill=("RowFill", "first"),
                  D_Remark=("Remark", "max"), D_TypedCode=("TypedCode", "max"))
             )
    rows = attrs.set_index(KEY).join(names).join(srcrow).join(dmeta).reset_index()
    for c in ("P_Name", "D_Name", "I_Name"):
        if c not in rows:
            rows[c] = np.nan
    rows["P_Exists"], rows["D_Exists"], rows["I_Exists"] = rows["P_Name"].notna(), rows["D_Name"].notna(), rows["I_Name"].notna()

    grid = rows[KEY].merge(pd.DataFrame({"Month": months}), how="cross")
    def scen(name, cols):
        s = df[df["Scenario"] == name].groupby(KEY + ["Month"]).agg(**cols).reset_index()
        return s
    P = scen("Planning Input", {"P": ("MH", "sum")})
    D = scen("Discipline Comment", {"D": ("MH", lambda x: x.sum(min_count=1)), "D_Blank": ("IsBlank", "all"),
                                     "D_Unknown": ("ZeroOrBlankUnknown", "any"), "D_Red": ("FontRed", "any"),
                                     "D_Fill": ("CellFill", "max"), "D_Comment": ("CellComment", "max")})
    I = scen("System Implementation", {"I": ("MH", "sum")})
    c = grid.merge(P, on=KEY + ["Month"], how="left").merge(D, on=KEY + ["Month"], how="left").merge(I, on=KEY + ["Month"], how="left")
    c = c.merge(rows[KEY + ["P_Exists", "D_Exists", "I_Exists"]], on=KEY)
    c["P0"] = c["P"].fillna(0.0)
    c["I0"] = c["I"].fillna(0.0)
    c["Final"] = np.where(c["D_Exists"], np.where(c["P_Exists"], c["D"].fillna(c["P0"]), c["D"].fillna(0.0)), c["P0"])
    c["Change"] = (c["Final"] - c["P0"]).round(2)
    c["Changed"] = c["Change"].abs() >= TOL
    marked = c["D_Red"].fillna(False).astype(bool) | c["D_Fill"].fillna("").ne("")
    c["AmbiguousBlank"] = (c["D_Exists"] & c["P_Exists"] & (c["P0"] > 0) & c["D"].isna() &
                           (c["D_Unknown"].fillna(False).astype(bool) | marked))
    c["BlankRetained"] = c["D_Exists"] & c["P_Exists"] & (c["P0"] > 0) & c["D"].isna()
    c["AmbiguousReason"] = np.select(
        [c["AmbiguousBlank"] & c["D_Unknown"].fillna(False).astype(bool), c["AmbiguousBlank"]],
        ["Blank/zero indistinguishable in Copilot table - baseline retained",
         "Blank cell marked red/highlighted by department - possible removal, baseline retained"], "")
    # implementation of requested change
    req, got = c["Change"], c["I0"] - c["P0"]
    share = np.where(c["Changed"] & (np.sign(req) == np.sign(got)), np.minimum(np.abs(got), np.abs(req)) / np.abs(req).replace(0, np.nan), 0.0)
    c["ImplShare"] = np.where(c["Changed"], np.nan_to_num(share), np.nan)
    c["ImplGap"] = np.where(c["Changed"], (c["I0"] - c["Final"]).round(2), 0.0)
    c["Unrequested"] = np.where(~c["Changed"], (c["I0"] - c["Final"]).round(2), 0.0)
    c["CellStatus"] = np.select(
        [c["Changed"] & ((c["I0"] - c["Final"]).abs() < TOL),
         c["Changed"] & ((c["I0"] - c["P0"]).abs() < TOL),
         c["Changed"] & (c["ImplShare"] > 0),
         c["Changed"],
         ~c["Changed"] & (c["Unrequested"].abs() >= TOL)],
        ["Implemented", "Not implemented", "Partially implemented", "Implemented differently",
         "Planner change without comment"], "No change")
    rows, transfers = _classify_rows(rows, c, months)
    return c, rows, transfers


def _span(vals: np.ndarray, months: list) -> tuple:
    nz = np.nonzero(np.abs(vals) >= TOL)[0]
    return (months[nz[0]], months[nz[-1]]) if len(nz) else (None, None)


def _classify_rows(rows: pd.DataFrame, c: pd.DataFrame, months: list) -> pd.DataFrame:
    c = c.sort_values(KEY + ["Month"])
    g = c.groupby(KEY, sort=False)
    agg = g.agg(P_Total=("P0", "sum"), Final_Total=("Final", "sum"), I_Total=("I0", "sum"),
                Gross_Increase=("Change", lambda x: x[x > 0].sum()), Gross_Reduction=("Change", lambda x: x[x < 0].sum()),
                Net_Change=("Change", "sum"), Changed_Cells=("Changed", "sum"),
                Ambiguous_Cells=("AmbiguousBlank", "sum"),
                BlankRetained_MH=("P0", lambda x: 0.0),
                Ambiguous_MH=("P0", lambda x: 0.0),
                Unrequested_Gross=("Unrequested", lambda x: x.abs().sum()), Unrequested_Net=("Unrequested", "sum"),
                Impl_Gap=("ImplGap", "sum")).reset_index()
    amb = c[c["AmbiguousBlank"]].groupby(KEY)["P0"].sum().rename("Ambiguous_MH_")
    agg = agg.drop(columns="Ambiguous_MH").merge(amb.reset_index(), on=KEY, how="left").rename(columns={"Ambiguous_MH_": "Ambiguous_MH"})
    agg["Ambiguous_MH"] = agg["Ambiguous_MH"].fillna(0.0)
    blk = c[c["BlankRetained"]].groupby(KEY)["P0"].sum().rename("BlankRetained_MH_")
    agg = agg.drop(columns="BlankRetained_MH").merge(blk.reset_index(), on=KEY, how="left").rename(
        columns={"BlankRetained_MH_": "BlankRetained_MH"})
    agg["BlankRetained_MH"] = agg["BlankRetained_MH"].fillna(0.0)
    w = c.assign(W=c["Change"].abs() * c["ImplShare"].fillna(0)).groupby(KEY).agg(W=("W", "sum")).reset_index()
    agg = agg.merge(w, on=KEY)
    agg["Impl_Ratio"] = np.where((agg["Gross_Increase"] - agg["Gross_Reduction"]) > 0,
                                 agg["W"] / (agg["Gross_Increase"] - agg["Gross_Reduction"]).replace(0, np.nan), np.nan)
    ext, early, accel, later, within_inc, within_red, fP, lP, fF, lF = ([] for _ in range(10))
    for _, grp in g:
        pv, fv, ch = grp["P0"].to_numpy(), grp["Final"].to_numpy(), grp["Change"].to_numpy()
        a, b = _span(pv, months)
        fa, fb = _span(fv, months)
        fP.append(a); lP.append(b); fF.append(fa); lF.append(fb)
        if a is None:
            ext.append(0.0); early.append(0.0); within_inc.append(ch[ch > 0].sum()); within_red.append(ch[ch < 0].sum())
            accel.append(False); later.append(False)
            continue
        mo = np.array(months)
        after, before, inside = mo > b, mo < a, (mo >= a) & (mo <= b)
        ext.append(ch[after & (ch > 0)].sum()); early.append(ch[before & (ch > 0)].sum())
        within_inc.append(ch[inside & (ch > 0)].sum()); within_red.append(ch[inside & (ch < 0)].sum())
        accel.append(fb is not None and fb < b); later.append(fa is not None and fa > a)
    agg["Extension_MH"], agg["EarlyStart_MH"] = ext, early
    agg["WithinSchedule_Increase"], agg["WithinSchedule_Reduction"] = within_inc, within_red
    agg["Finish_Earlier"], agg["Start_Later"] = accel, later
    agg["P_First"], agg["P_Last"], agg["Final_First"], agg["Final_Last"] = fP, lP, fF, lF
    months_changed = c[c["Changed"]].groupby(KEY)["Month"].apply(lambda s: ", ".join(m.strftime("%b-%y") for m in sorted(s)))
    agg = agg.merge(months_changed.rename("Months_Changed").reset_index(), on=KEY, how="left")
    rows = rows.merge(agg, on=KEY, how="left")

    # base project known in this department's baseline (any phase/section)?
    base_in_dept = set(zip(rows.loc[rows["P_Exists"], "DeptCode"], rows.loc[rows["P_Exists"], "ProjectBase"]))
    base_any = set(rows.loc[rows["P_Exists"], "ProjectBase"])
    # phase transfers at department + base project level
    pt = rows.groupby(["DeptCode", "ProjectBase", "Phase"])["Net_Change"].sum().reset_index()
    tr = pt.groupby(["DeptCode", "ProjectBase"]).apply(
        lambda x: pd.Series({"pos": x.loc[x.Net_Change > TOL, "Net_Change"].sum(),
                             "neg": x.loc[x.Net_Change < -TOL, "Net_Change"].sum(),
                             "phases_up": ", ".join(x.loc[x.Net_Change > TOL, "Phase"]),
                             "phases_down": ", ".join(x.loc[x.Net_Change < -TOL, "Phase"])}), include_groups=False).reset_index()
    tr["Transfer_MH"] = np.where((tr["pos"] > 0) & (tr["neg"] < 0), np.minimum(tr["pos"], -tr["neg"]), 0.0)
    tr = tr[tr["Transfer_MH"] > 0]
    tset = {(r.DeptCode, r.ProjectBase): r for r in tr.itertuples()}

    typed = rows["D_TypedCode"].fillna("")
    conflict = (typed != "") & (typed != rows["ProjectFull"]) & (typed.str.split("-").str[0] != rows["ProjectBase"])
    rows.loc[conflict & (rows["CodeCorrection"].fillna("") == ""), "CodeCorrection"] = (
        rows["ProjectFull"] + " -> " + typed + " (typed in column A)")
    rows["CodeCorrection"] = rows["CodeCorrection"].fillna("")
    types, concern = [], []
    for r in rows.itertuples():
        t = []
        moved = (r.DeptCode, r.ProjectBase) in tset and abs(r.Net_Change) >= TOL
        if r.D_Exists and not r.P_Exists and r.Final_Total >= TOL:
            if "#" in r.ProjectKey:
                t.append("New Project (code reused - verify)")
            else:
                t.append("New Phase Allocation" if (r.DeptCode, r.ProjectBase) in base_in_dept else "New Project")
        elif r.P_Exists and r.D_Exists:
            if r.CodeCorrection:
                t.append("Project Code Correction")
            if r.D_Remark and not r.Changed_Cells and not r.CodeCorrection:
                t.append("Description Change Only")
            if r.RequestedPhase:
                t.append("Phase Transfer" if r.Changed_Cells else "Phase Label Change Only")
            if r.P_Total >= TOL and r.Final_Total < TOL and r.Changed_Cells:
                t.append("Removed Allocation")
            elif r.Changed_Cells:
                inc, red, net = r.Gross_Increase, -r.Gross_Reduction, r.Net_Change
                if inc >= TOL and red >= TOL and abs(net) <= 0.1 * (inc + red):
                    t.append("Monthly Redistribution")
                else:
                    t.append("Increased MH" if net > 0 else "Reduced MH")
                    if inc >= TOL and red >= TOL:
                        t.append("Monthly Redistribution")
                if r.Extension_MH >= TOL:
                    t.append("Schedule Extension")
                if r.EarlyStart_MH >= TOL:
                    t.append("Earlier Start")
                if r.Finish_Earlier:
                    t.append("Schedule Acceleration")
                if r.Start_Later:
                    t.append("Rescheduled (later start)")
            if not r.Changed_Cells and isinstance(r.P_Name, str) and isinstance(r.D_Name, str) and _sim(r.P_Name, r.D_Name) < 0.85:
                t.append("Description Change Only")
        elif r.P_Exists and not r.D_Exists:
            t.append("Not in Feedback (retained)")
        if moved:
            t.append("Phase Transfer")
        types.append("; ".join(dict.fromkeys(t)))
        if r.CodeCorrection and "Project Code Correction" not in t:
            t.append("Project Code Correction")
        concern.append(bool(r.Changed_Cells) or (r.D_Exists and not r.P_Exists and r.Final_Total >= TOL)
                       or (bool(r.CodeCorrection) and r.D_Exists) or (bool(r.RequestedPhase) and r.D_Exists))
    rows["Concern_Types"], rows["Concern"] = types, concern
    rows["Primary_Type"] = rows["Concern_Types"].str.split("; ").str[0].replace("", "No Change")
    rows["Known_In_Other_Depts"] = [(not r.P_Exists) and (r.ProjectBase in base_any) and
                                    ((r.DeptCode, r.ProjectBase) not in base_in_dept) for r in rows.itertuples()]
    no_mh = (rows["Gross_Increase"] - rows["Gross_Reduction"]).abs() < TOL
    rows["Implementation_Status"] = np.select(
        [rows["Concern"] & no_mh,
         rows["Concern"] & ~rows["I_Exists"],
         rows["Concern"] & (rows["Impl_Ratio"] >= 0.95),
         rows["Concern"] & (rows["Impl_Ratio"] >= 0.05),
         rows["Concern"],
         ~rows["Concern"] & (rows["Unrequested_Gross"] >= TOL) & rows["I_Exists"] & ~rows["P_Exists"] & ~rows["D_Exists"],
         ~rows["Concern"] & (rows["Unrequested_Gross"] >= TOL)],
        ["Code or phase change - confirm in Primavera", "Not implemented (row absent after implementation)",
         "Implemented", "Partially implemented",
         "Not implemented", "Added by Planning without comment", "Planner change without comment"], "No comment / no change")
    rows["Transfer_MH"] = [tset[(r.DeptCode, r.ProjectBase)].Transfer_MH if (r.DeptCode, r.ProjectBase) in tset and abs(r.Net_Change) >= TOL else 0.0
                           for r in rows.itertuples()]
    # net-deviation bridge: every row's net change lands in exactly one bucket set, so the buckets add up to net
    new = rows["D_Exists"] & ~rows["P_Exists"]
    removed = rows["Concern_Types"].str.contains("Removed Allocation")
    other = ~new & ~removed
    rows["Bridge_New_Work"] = np.where(new, rows["Net_Change"], 0.0)
    rows["Bridge_Removed"] = np.where(removed, rows["Net_Change"], 0.0)
    rows["Bridge_Extension"] = np.where(other, rows["Extension_MH"] + rows["EarlyStart_MH"], 0.0)
    rows["Bridge_Increase"] = np.where(other, rows["WithinSchedule_Increase"], 0.0)
    rows["Bridge_Reduction"] = np.where(other, rows["WithinSchedule_Reduction"], 0.0)
    rows["Brief_Categories"] = rows["Concern_Types"].map(brief_categories)
    return rows, tr


# the brief's eleven concern categories; engine sub-types stay available as detail
BRIEF_MAP = {"Increased MH": "Increased MH", "Reduced MH": "Reduced MH", "New Project": "New Project",
             "New Project (code reused - verify)": "New Project", "New Phase Allocation": "New Phase Allocation",
             "Phase Transfer": "Phase Transfer", "Phase Label Change Only": "Phase Transfer",
             "Schedule Extension": "Schedule Extension", "Schedule Acceleration": "Schedule Acceleration",
             "Earlier Start": "Schedule Acceleration", "Rescheduled (later start)": "Monthly Redistribution",
             "Monthly Redistribution": "Monthly Redistribution", "Project Code Correction": "Project Code Correction",
             "Removed Allocation": "Removed Allocation", "Description Change Only": "Description Change Only"}


def brief_categories(types: str) -> str:
    return "; ".join(dict.fromkeys(BRIEF_MAP[t] for t in str(types or "").split("; ") if t in BRIEF_MAP))


def severity(projects: pd.DataFrame) -> pd.DataFrame:
    """Points-based severity (documented in the workbook): magnitude, %, breadth, duration, new, extension."""
    p = projects.copy()
    gross = p["Gross_Increase"] - p["Gross_Reduction"]
    pct = np.where(p["P_Total"] > 0, (p["Net_Change"] / p["P_Total"]).abs(), np.inf)
    pts = (np.select([gross >= 50000, gross >= 20000, gross >= 10000, gross >= 5000, gross >= 2000, gross >= 500],
                     [6, 5, 4, 3, 2, 1], 0)
           + np.select([~np.isfinite(pct), pct >= 0.5, pct >= 0.2], [2, 2, 1], 0)
           + np.select([p["Depts_Concerned"] >= 6, p["Depts_Concerned"] >= 4, p["Depts_Concerned"] >= 2], [3, 2, 1], 0)
           + np.select([p["Months_Affected"] >= 12, p["Months_Affected"] >= 6], [2, 1], 0)
           + p["Has_New_Allocation"].astype(int) + p["Has_Extension"].astype(int))
    p["Severity_Points"] = np.where(p["Concern_Rows"] > 0, pts, 0)
    p["Severity"] = np.select([p["Concern_Rows"] == 0, p["Severity_Points"] >= 10, p["Severity_Points"] >= 7,
                               p["Severity_Points"] >= 4], ["No Concern", "Critical", "High", "Medium"], "Low")
    p["Net_Pct"] = np.where(p["P_Total"] > 0, p["Net_Change"] / p["P_Total"].replace(0, np.nan), np.nan)
    return p
