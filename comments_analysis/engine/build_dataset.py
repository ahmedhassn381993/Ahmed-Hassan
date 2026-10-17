"""Build one clean, cell-level table for the three forecast-comment scenarios.

Scenarios
    1  Planning Input          - workbooks sent to departments ("To Departments" / before)
    2  Discipline Comment      - department feedback workbooks (-c)
    3  System Implementation   - workbooks re-extracted after planners implemented comments (after)

Sources (either or both)
    * Department workbooks parsed directly (preferred: keeps blank vs zero and red/yellow marks).
    * The Copilot "PowerBI_Workload_Model_3_Scenarios" workbook (fallback for departments whose
      workbooks are not available). Its defects are corrected here and logged:
        - department/sub-department total rows were loaded as projects (double counting)
        - a 'Total' column in 2800-c was loaded as an extra month
        - project codes truncated to 7 characters by the =LEFT(B,7) template formula
        - Piping 2410-2450 tagged 2400 and 2720 tagged 2710 in two of three scenarios
        - blank feedback cells stored as 0 (blank and explicit zero are indistinguishable)
"""
from __future__ import annotations

import datetime as dt
import re
import xml.etree.ElementTree as ET
import zipfile
from dataclasses import asdict
from pathlib import Path

import pandas as pd

from workbook_parser import INTERNAL_CODE, parse_project_code, parse_workbook

SCENARIOS = {"Planning Input": 1, "Discipline Comment": 2, "System Implementation": 3}
PHASE_ORDER = {"Running": 1, "Loan To": 2, "Under Signature": 3, "Proposal": 4, "Potential Opportunity": 5}
FACT_PHASE = {"Proposed": "Proposal"}
NS = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"

COLUMNS = [
    "Scenario", "ScenarioOrder", "DeptCode", "SectionCode", "Phase", "Category",
    "ProjectFull", "ProjectBase", "ProjectSuffix", "ActivityName", "Month", "MH", "IsBlank",
    "ZeroOrBlankUnknown", "FontRed", "CellFill", "RowFill", "CellComment", "SourceFile",
    "SourceRow", "SourceType",
]


def _read_sheet_raw(xlsx: Path, sheet_name: str) -> pd.DataFrame:
    """Read a worksheet without applying number formats (the Copilot file formats codes as dates)."""
    with zipfile.ZipFile(xlsx) as z:
        wb = ET.fromstring(z.read("xl/workbook.xml"))
        rels = ET.fromstring(z.read("xl/_rels/workbook.xml.rels"))
        rid = next(s.get("{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id")
                   for s in wb.iter(f"{NS}sheet") if s.get("name") == sheet_name)
        target = next(r.get("Target") for r in rels if r.get("Id") == rid)
        target = target.lstrip("/")
        target = target if target.startswith("xl/") else f"xl/{target}"
        strings = []
        if "xl/sharedStrings.xml" in z.namelist():
            for si in ET.fromstring(z.read("xl/sharedStrings.xml")).iter(f"{NS}si"):
                strings.append("".join(t.text or "" for t in si.iter(f"{NS}t")))
        rows = []
        with z.open(target) as fh:
            for _, el in ET.iterparse(fh):
                if el.tag != f"{NS}row":
                    continue
                rec = {}
                for c in el.iter(f"{NS}c"):
                    ref = re.match(r"[A-Z]+", c.get("r")).group(0)
                    idx = 0
                    for ch in ref:
                        idx = idx * 26 + ord(ch) - 64
                    v, t = c.find(f"{NS}v"), c.get("t")
                    if t == "s" and v is not None:
                        val = strings[int(v.text)]
                    elif t == "inlineStr":
                        val = "".join(x.text or "" for x in c.iter(f"{NS}t"))
                    elif v is None:
                        val = None
                    elif t in ("str", "e"):
                        val = v.text
                    else:
                        try:
                            val = float(v.text)
                        except ValueError:
                            val = v.text
                    rec[idx - 1] = val
                rows.append(rec)
                el.clear()
    width = max((max(r) for r in rows if r), default=-1) + 1
    data = [[r.get(i) for i in range(width)] for r in rows]
    return pd.DataFrame(data[1:], columns=data[0])


def dept_of(source_file: str) -> str:
    m = re.match(r"^(\d{3,4})", Path(source_file).name)
    return m.group(1) if m else Path(source_file).stem


def _assign_sections(rows: pd.DataFrame, header_codes: dict[int, str], dept: str) -> pd.Series:
    """Section = latest sub-department header above the row; if a file has no headers, a new
    section starts whenever the phase sequence restarts (e.g. Potential Opportunity -> Running)."""
    sections, current, ordinal, last_phase = [], dept, 0, 0
    header_rows = sorted(header_codes)
    for src_row, phase in zip(rows["SourceRow"], rows["Phase"]):
        hdr = [h for h in header_rows if h < src_row]
        p = PHASE_ORDER.get(phase, 0)
        if hdr:
            current = header_codes[hdr[-1]]
        elif p < last_phase:
            ordinal += 1
            current = f"{dept}#S{ordinal + 1}"
        last_phase = p
        sections.append(current)
    return pd.Series(sections, index=rows.index)


def load_copilot_fact(xlsx: str | Path, issues: list[dict]) -> pd.DataFrame:
    xlsx = Path(xlsx)
    raw = _read_sheet_raw(xlsx, "Fact_Workload_Long")
    raw["MonthDate"] = pd.to_datetime(raw["Month"], unit="D", origin="1899-12-30")
    bad_month = raw["MonthDate"].dt.year < 2000
    for (sc, fn), g in raw[bad_month].groupby(["Scenario", "Source File"]):
        issues.append(dict(Severity="High", Area="Copilot model", Scenario=sc, File=fn,
                           Issue="Non-month column loaded as a month (e.g. a 'Total' column)",
                           Detail=f"{len(g)} cells / {g['MHrs'].sum():,.0f} MH excluded",
                           Impact_MH=float(g["MHrs"].sum())))
    raw = raw[~bad_month].copy()
    out = []
    for (sc, fn), g in raw.groupby(["Scenario", "Source File"], sort=False):
        dept = dept_of(fn)
        g = g.sort_values(["Source Row", "MonthDate"])
        rows = g.drop_duplicates("Source Row")[["Source Row", "Project", "Activity Name", "Status Indicator"]].copy()
        rows.columns = ["SourceRow", "ProjectCell", "ActivityName", "StatusIndicator"]
        rows["SourceRow"] = rows["SourceRow"].astype(int)
        # numeric column-A values are either LEFT(B,7) results or typed codes; only 7-8 digit numbers
        # are usable, and only when the activity text carries no code itself
        def col_a(p):
            if isinstance(p, float):
                return str(int(p)) if 1_000_000 <= p < 100_000_000 else None
            return p
        codes = [parse_project_code(None, str(a or "")) for a in rows["ActivityName"]]
        codes = [c if c.full else parse_project_code(col_a(p), "") for c, p in zip(codes, rows["ProjectCell"])]
        rows["ProjectFull"] = [c.full for c in codes]
        rows["ProjectBase"] = [c.base for c in codes]
        rows["ProjectSuffix"] = [c.suffix for c in codes]
        is_header = (rows["ProjectFull"] == "") & rows["ProjectCell"].apply(
            lambda v: isinstance(v, float) and v < 10000)
        header_codes = {int(r): str(int(p)) for r, p, h in zip(rows["SourceRow"], rows["ProjectCell"], is_header) if h}
        hdr_mh = g[g["Source Row"].astype(int).isin(header_codes)]
        if len(hdr_mh) and hdr_mh["MHrs"].sum():
            issues.append(dict(Severity="High", Area="Copilot model", Scenario=sc, File=fn,
                               Issue="Department / sub-department total rows loaded as project rows",
                               Detail=f"rows {sorted(header_codes)} excluded",
                               Impact_MH=float(hdr_mh["MHrs"].sum())))
        unknown = rows[(rows["ProjectFull"] == "") & ~is_header]
        for _, r in unknown.iterrows():
            issues.append(dict(Severity="Medium", Area="Project code", Scenario=sc, File=fn,
                               Issue="Project code not identified", Detail=f"row {r.SourceRow}: {r.ActivityName}",
                               Impact_MH=float(g[g["Source Row"].astype(int) == r.SourceRow]["MHrs"].sum())))
        rows = rows[~is_header].copy()
        rows["Phase"] = rows["StatusIndicator"].replace(FACT_PHASE)
        rows = rows[rows["Phase"].isin(PHASE_ORDER)]
        # keep sub-department headers (5110..5160 in 5100); drop the file's own and parent headers (2400 in 2410)
        dept_headers = {k: v for k, v in header_codes.items()
                        if v != dept and not (v.endswith("00") and v[:2] == dept[:2])}
        rows["SectionCode"] = _assign_sections(rows, dept_headers, dept)
        cells = g[["Source Row", "MonthDate", "MHrs"]].copy()
        cells.columns = ["SourceRow", "Month", "MH"]
        cells["SourceRow"] = cells["SourceRow"].astype(int)
        cells = cells.merge(rows, on="SourceRow", how="inner")
        cells["Scenario"], cells["DeptCode"], cells["SourceFile"] = sc, dept, fn
        out.append(cells)
    df = pd.concat(out, ignore_index=True)
    df["Month"] = df["Month"].dt.date
    df["ActivityName"] = df["ActivityName"].astype(str).str.replace(r"\s+", " ", regex=True).str.strip()
    is_comment = df["Scenario"] == "Discipline Comment"
    df["ZeroOrBlankUnknown"] = is_comment & (df["MH"] == 0)
    df.loc[df["ZeroOrBlankUnknown"], "MH"] = float("nan")   # blank feedback = no revision
    df["IsBlank"] = df["MH"].isna()
    df["FontRed"], df["CellFill"], df["RowFill"], df["CellComment"] = False, "", "", ""
    df["SourceType"] = "Copilot fact table (validated)"
    return _finish(df)


def load_workbook_folder(folder: str | Path, scenario: str, issues: list[dict]) -> pd.DataFrame:
    frames = []
    for path in sorted(Path(folder).glob("*.xlsx")):
        if path.name.startswith("~$"):
            continue
        pw = parse_workbook(path)
        for i in pw.issues:
            issues.append(dict(Severity=i["severity"], Area="Source workbook", Scenario=scenario,
                               File=i["file"], Issue=i["issue"], Detail=f"row {i['row']}: {i['detail']}",
                               Impact_MH=None))
        if not pw.records:
            continue
        d = pd.DataFrame([asdict(r) for r in pw.records])
        frames.append(pd.DataFrame({
            "Scenario": scenario, "DeptCode": pw.dept_file, "SectionCode": d["section_code"],
            "Phase": d["phase"], "ProjectFull": d["project_full"], "ProjectBase": d["project_base"],
            "ProjectSuffix": d["project_suffix"],
            "ActivityName": d["activity"].str.replace(r"\s+", " ", regex=True).str.strip(),
            "Month": d["month"], "MH": d["value"], "IsBlank": d["value"].isna(),
            "ZeroOrBlankUnknown": False, "FontRed": d["font_red"], "CellFill": d["cell_fill"],
            "RowFill": d["row_fill"], "CellComment": d["comment"], "SourceFile": d["file"],
            "SourceRow": d["row"], "SourceType": "Source workbook",
        }))
    if not frames:
        return pd.DataFrame(columns=COLUMNS)
    df = pd.concat(frames, ignore_index=True)
    if scenario != "Discipline Comment":
        df["MH"] = df["MH"].fillna(0.0)   # system extracts: blank means zero
    return _finish(df)


def _finish(df: pd.DataFrame) -> pd.DataFrame:
    df["ScenarioOrder"] = df["Scenario"].map(SCENARIOS)
    df["Category"] = df["Phase"]
    df.loc[(df["Phase"] == "Running") & (df["ProjectBase"] == INTERNAL_CODE), "Category"] = "Internal / Unforeseen"
    df["SourceRow"] = df["SourceRow"].astype(int)
    return df[COLUMNS]


def _cells(g: pd.DataFrame) -> set:
    g = g[g["MH"].fillna(0) != 0]
    return set(zip(g["ProjectFull"], g["Phase"], g["Month"], g["MH"].round(2)))


def _resolve_inferred_sections(df: pd.DataFrame, issues: list[dict]) -> pd.DataFrame:
    """Blocks without sub-department headers (seen in the after-implementation extracts):
    1. a block that reproduces another department's file is a duplicate -> excluded
       (after/1100 repeats 1180, after/2300 repeats 2800, after/5100 repeats 5000);
    2. remaining blocks are matched to the Planning Input sections by project-set similarity."""
    inferred_depts = df.loc[df["SectionCode"].str.contains("#S", regex=False), ["Scenario", "DeptCode"]].drop_duplicates()
    drop = pd.Series(False, index=df.index)
    for sc, dept in inferred_depts.itertuples(index=False):
        idx = (df["Scenario"] == sc) & (df["DeptCode"] == dept)
        blocks = list(dict.fromkeys(df.loc[idx].sort_values("SourceRow")["SectionCode"]))
        others = {d: _cells(g) for d, g in df[(df["Scenario"] == sc) & (df["DeptCode"] != dept)].groupby("DeptCode")}
        keep = []
        for b in blocks:
            bidx = idx & (df["SectionCode"] == b)
            sig = _cells(df[bidx])
            dup = next((d for d, s in others.items() if sig and s and len(sig & s) / max(len(sig), len(s)) >= 0.95), None)
            if dup:
                drop |= bidx
                issues.append(dict(Severity="High", Area="Double counting", Scenario=sc, File=f"{dept}.xlsx",
                                   Issue=f"Block in {dept} file duplicates department {dup} file - excluded",
                                   Detail=f"block {b}: {int(bidx.sum() / 24)} rows identical to {dup}.xlsx",
                                   Impact_MH=float(df.loc[bidx, "MH"].fillna(0).sum())))
            else:
                keep.append(b)
        plan = df[(df["Scenario"] == "Planning Input") & (df["DeptCode"] == dept)]
        targets = list(dict.fromkeys(plan.sort_values("SourceRow")["SectionCode"]))
        tsets = {t: set(zip(plan.loc[plan["SectionCode"] == t, "ProjectFull"], plan.loc[plan["SectionCode"] == t, "Phase"])) for t in targets}
        mapping, free = {}, list(targets)
        for b in keep:
            bset = set(zip(df.loc[idx & (df["SectionCode"] == b), "ProjectFull"], df.loc[idx & (df["SectionCode"] == b), "Phase"]))
            if not free:
                mapping[b] = b
                continue
            best = max(free, key=lambda t: len(bset & tsets[t]) / max(1, len(bset | tsets[t])))
            mapping[b] = best
            free.remove(best)
        df.loc[idx & ~drop, "SectionCode"] = df.loc[idx & ~drop, "SectionCode"].map(mapping)
        issues.append(dict(Severity="Low", Area="Assumption", Scenario=sc, File=f"{dept}.xlsx",
                           Issue="Sub-department blocks without headers matched by project similarity",
                           Detail=f"{mapping}", Impact_MH=None))
    return df[~drop].reset_index(drop=True)


def build(copilot_fact: str | None = None, folders: dict[str, str] | None = None,
          window: tuple[dt.date, dt.date] | None = None) -> tuple[pd.DataFrame, list[dict]]:
    """Combine sources; workbook folders override the Copilot fact per (scenario, department)."""
    issues: list[dict] = []
    frames = []
    direct = pd.DataFrame(columns=COLUMNS)
    if folders:
        parts = [load_workbook_folder(p, sc, issues) for sc, p in folders.items() if p and Path(p).exists()]
        parts = [p for p in parts if len(p)]
        if parts:
            direct = pd.concat(parts, ignore_index=True)
    if copilot_fact:
        fact_issues: list[dict] = []
        fact = load_copilot_fact(copilot_fact, fact_issues)
        covered = set(zip(direct["Scenario"], direct["DeptCode"])) if len(direct) else set()
        if covered:
            fact = fact[[(s, d) not in covered for s, d in zip(fact["Scenario"], fact["DeptCode"])]]
        # keep Copilot-model findings for every file (they explain its totals); drop only the
        # row-level code warnings for files that were re-read from source
        issues += [i for i in fact_issues if not (i["Area"] == "Project code" and (i["Scenario"], dept_of(i["File"])) in covered)]
        frames.append(fact)
    if len(direct):
        frames.append(direct)
    df = pd.concat(frames, ignore_index=True)
    if window is None:
        months = sorted(df.loc[df["Scenario"] == "Planning Input", "Month"].unique())
        window = (months[0], months[-1])
    outside = (df["Month"] < window[0]) | (df["Month"] > window[1])
    for sc, g in df[outside].groupby("Scenario"):
        issues.append(dict(Severity="Medium", Area="Forecast window", Scenario=sc, File="(all)",
                           Issue="Months outside the comparison window excluded",
                           Detail=f"{sorted({m.strftime('%b-%y') for m in g['Month']})}",
                           Impact_MH=float(g["MH"].fillna(0).sum())))
    df = df[~outside].reset_index(drop=True)
    df = _resolve_inferred_sections(df, issues)
    return df, issues
