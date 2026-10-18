"""Build the 'Workload Comments Analysis' Power BI template (.pbit) and regenerate queries.pq from one model spec.

    python build_pbit.py --out Workload_Comments_Analysis.pbit --pbix Forecast_Comments.pbix

Opening the template in Power BI Desktop asks for OutputRoot (the folder that holds one sub-folder per cycle,
as written by engine/run_analysis.py) and loads every cycle's CSV files. The template carries the data model
(queries, relationships, sort orders, the measures in measures.dax), the ENPPI theme and nine report pages
built only from Power BI's standard visuals, in the 1280 x 720 layout of the existing workload report.
--pbix copies the ENPPI logo and the base theme from that report; without it the header shows a text logo.
"""
from __future__ import annotations

import argparse
import json
import re
import uuid
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
NS = uuid.UUID("6f1c8a52-3c1e-4a7e-9a55-2d3b8f0c1e77")      # stable lineage tags between rebuilds

T, I, N, D = "text", "int", "number", "date"
M_TYPE = {T: "type text", I: "Int64.Type", N: "type number", D: "type date"}
TOM_TYPE = {T: "string", I: "int64", N: "double", D: "dateTime"}

# table -> csv, columns, distinct key (dimensions), hidden columns, sort-by pairs
TABLES = {
    "FactWorkload": dict(csv="FactWorkload.csv", cols=[
        ("Cycle", T), ("Scenario", T), ("DeptCode", I), ("Category", T), ("ProjectCode", T), ("RowKey", T),
        ("Month", D), ("MH", N)], hidden=["RowKey"]),
    "FactCommentItems": dict(csv="FactCommentItems.csv", cols=[
        ("Cycle", T), ("RowKey", T), ("CycleRowKey", T), ("DeptCode", I), ("SubDept", T), ("Category", T),
        ("ProjectCode", T), ("Activity", T), ("Item Planning MH", N), ("Item Discipline MH", N), ("Item Implemented MH", N),
        ("Gross Increase", N), ("Gross Reduction", N), ("Net Change", N), ("Concern", T), ("Primary Type", T),
        ("Concern Types", T), ("Implementation Status", T), ("Item Implementation Rate", N), ("Implemented Request MH", N),
        ("Unrequested Change MH", N), ("Requested Phase", T), ("Code Correction", T), ("Months Changed", T),
        ("Blank Not Applied MH", N), ("Feedback File", T), ("Feedback Row", I), ("Brief Categories", T),
        ("Department Remark", T), ("Blank Retained MH", N), ("Bridge New Work MH", N), ("Bridge Increase MH", N),
        ("Bridge Extension MH", N), ("Bridge Reduction MH", N), ("Bridge Removed MH", N), ("Master Flag", T)],
        hidden=["RowKey", "CycleRowKey"]),
    "FactCellChanges": dict(csv="FactCellChanges.csv", cols=[
        ("Cycle", T), ("RowKey", T), ("CycleRowKey", T), ("DeptCode", I), ("ProjectCode", T), ("Month", D),
        ("Cell Planning MH", N), ("Discipline Value", N), ("Cell Discipline MH", N), ("Change MH", N), ("Cell Implemented MH", N),
        ("Cell Status", T), ("Blank Note", T)], hidden=["RowKey", "CycleRowKey"]),
    "BridgeConcernType": dict(csv="BridgeConcernType.csv", cols=[
        ("Cycle", T), ("RowKey", T), ("CycleRowKey", T), ("ConcernType", T), ("Brief Category", T)],
        hidden=["RowKey", "CycleRowKey", "Cycle"]),
    "DimDepartment": dict(csv="DimDepartment.csv", cols=[
        ("DeptCode", I), ("Department", T), ("Division", I), ("Service", T), ("Dept Label", T), ("Managers", I),
        ("LoanTo_Unpaid", I), ("Permanent", I), ("Temporary", I), ("Total", I), ("Effective_Heads", I),
        ("Capacity_MH_per_Month", N), ("Feedback Status", T), ("Feedback Processed", T), ("Sent To Dept", D),
        ("Comments Received", D), ("As Of", D)], key="DeptCode", latest="As Of"),
    "DimProject": dict(csv="DimProject.csv", cols=[
        ("ProjectCode", T), ("Project Name", T), ("Base Code", T), ("Client", T), ("Master Name", T),
        ("Master Client", T), ("Master Category", T), ("Master Flag", T), ("Title Variants", T), ("Project Label", T),
        ("Severity", T), ("Severity Points", I), ("Depts Concerned", I), ("Departments Raising", T),
        ("Severity Order", I)], key="ProjectCode", sort=[("Severity", "Severity Order")]),
    "DimCategory": dict(csv="DimCategory.csv", cols=[
        ("Category", T), ("Category Order", I), ("Parent Category", T), ("Firm Workload", T), ("Color", T)],
        key="Category", sort=[("Category", "Category Order")]),
    "DimScenario": dict(csv="DimScenario.csv", cols=[
        ("Scenario", T), ("Scenario Order", I), ("Color", T), ("Meaning", T)],
        key="Scenario", sort=[("Scenario", "Scenario Order")]),
    "DimDate": dict(csv="DimDate.csv", cols=[
        ("Month", D), ("Year", I), ("Quarter", T), ("Month Label", T), ("Month Index", I), ("Period", T)],
        key="Month", sort=[("Month Label", "Month")]),
    "DataQuality": dict(csv="DataQuality.csv", cols=[
        ("Cycle", T), ("Severity", T), ("Area", T), ("Scenario", T), ("File", T), ("Issue", T), ("Detail", T),
        ("Impact_MH", N)]),
    "FileInventory": dict(csv="FileInventory.csv", cols=[
        ("Dept", I), ("Department", T), ("Scenario", T), ("File Used", T), ("Source", T), ("Sent to Dept", D),
        ("Comments Received", D), ("Action Needed", T), ("Cycle", T)]),
    "PossibleTransfers": dict(csv="PossibleTransfers.csv", cols=[
        ("Dept", I), ("Reduced Project", T), ("Reduced Phase", T), ("Reduced Title", T), ("Reduction MH", N),
        ("Added Project", T), ("Added Phase", T), ("Added Title", T), ("Added MH", N), ("Match Basis", T),
        ("Possible Offset MH", N), ("Net of the Pair MH", N), ("Cycle", T)]),
}
# (from table, from column, to table, to column, both directions)
RELATIONSHIPS = [
    ("FactWorkload", "Scenario", "DimScenario", "Scenario", False),
    ("FactWorkload", "DeptCode", "DimDepartment", "DeptCode", False),
    ("FactWorkload", "Category", "DimCategory", "Category", False),
    ("FactWorkload", "ProjectCode", "DimProject", "ProjectCode", False),
    ("FactWorkload", "Month", "DimDate", "Month", False),
    ("FactWorkload", "Cycle", "DimCycle", "Cycle", False),
    ("FactCommentItems", "DeptCode", "DimDepartment", "DeptCode", False),
    ("FactCommentItems", "Category", "DimCategory", "Category", False),
    ("FactCommentItems", "ProjectCode", "DimProject", "ProjectCode", False),
    ("FactCommentItems", "Cycle", "DimCycle", "Cycle", False),
    ("FactCellChanges", "CycleRowKey", "FactCommentItems", "CycleRowKey", False),
    ("FactCellChanges", "Month", "DimDate", "Month", False),
    ("BridgeConcernType", "CycleRowKey", "FactCommentItems", "CycleRowKey", True),
    ("DataQuality", "Cycle", "DimCycle", "Cycle", False),
    ("FileInventory", "Cycle", "DimCycle", "Cycle", False),
    ("PossibleTransfers", "Cycle", "DimCycle", "Cycle", False),
]

FN_LOAD_CSV = """(fileName as text, columnTypes as list) as table =>
let
    Names    = List.Transform(columnTypes, each _{0}),
    Files    = Folder.Files(OutputRoot),
    Picked   = Table.SelectRows(Files, each [Name] = fileName),
    Parsed   = List.Transform(Picked[Content], each
                   Table.TransformColumnNames(        // drop a byte-order mark left on the first header
                       Table.PromoteHeaders(Csv.Document(_, [Delimiter = ",", Encoding = 65001, QuoteStyle = QuoteStyle.Csv]),
                                            [PromoteAllScalars = true]),
                       each Text.Replace(_, "#(FEFF)", ""))),
    Combined = if List.IsEmpty(Parsed) then #table(Names, {}) else Table.Combine(Parsed),
    // columns added in later cycles are null in older ones; unknown columns are dropped
    Complete = List.Accumulate(Names, Combined, (t, n) => if Table.HasColumns(t, n) then t else Table.AddColumn(t, n, each null)),
    Selected = Table.SelectColumns(Complete, Names),
    Blanks   = Table.ReplaceValue(Selected, "", null, Replacer.ReplaceValue, Names),
    Typed    = Table.TransformColumnTypes(Blanks, columnTypes, "en-US")
in
    Typed"""


def m_table(name: str, spec: dict) -> str:
    types = ",\n        ".join(f'{{"{c}", {M_TYPE[t]}}}' for c, t in spec["cols"])
    lines = ["let", f'    Source = fnLoadCsv("{spec["csv"]}", {{\n        {types}}})']
    last = "Source"
    if spec.get("latest"):
        lines[-1] += ","
        lines.append(f'    Sorted = Table.Buffer(Table.Sort(Source, {{{{"{spec["latest"]}", Order.Descending}}}}))')
        last = "Sorted"
    if spec.get("key"):
        lines[-1] += ","
        lines.append(f'    Latest = Table.Distinct({last}, {{"{spec["key"]}"}})')
        last = "Latest"
    return "\n".join(lines + ["in", f"    {last}"])


M_CYCLE = 'let\n    Source = Table.Distinct(Table.SelectColumns(FactWorkload, {"Cycle"}))\nin\n    Source'
M_MEASURES = 'let\n    Source = #table(type table [Measures = text], {})\nin\n    Source'


def write_queries(path: Path, root: str):
    out = ["// " + "=" * 85,
           "// Workload Comments Analysis - Power Query (M). Generated by build_pbit.py from the same spec as the",
           "// template, so the two never drift. To build by hand: Transform data > New Source > Blank Query >",
           "// Advanced Editor, one query per block, named as in the block title (create OutputRoot and fnLoadCsv first).",
           "// Every cycle sub-folder under OutputRoot is loaded. Codes stay TEXT (5305200, 40061420, 5020250-7).",
           "// " + "=" * 85, "",
           "// ---- OutputRoot (Manage Parameters > New, Type = Text) ----",
           f'"{root}" meta [IsParameterQuery=true, Type="Text", IsParameterQueryRequired=true]', "",
           "// ---- fnLoadCsv ----", FN_LOAD_CSV, ""]
    for name, spec in TABLES.items():
        out += [f"// ---- {name} ----", m_table(name, spec), ""]
    out += ["// ---- DimCycle ----", M_CYCLE, "", "// ---- _Measures (hide its only column) ----", M_MEASURES, ""]
    path.write_text("\n".join(out), encoding="utf-8")


# ------------------------------------------------------------------------------------------------ measures
def read_measures(path: Path) -> list[dict]:
    """Parse measures.dax: '// [n Folder]' starts a display folder; 'Name = expression' at column 0 starts a
    measure and indented lines continue it."""
    out, folder = [], ""
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        m = re.match(r"^//\s*\[\d+\s+(.+?)\]", line)
        if m:
            folder = m.group(1)
            continue
        if line.startswith("//"):
            continue
        if line[0] in " \t" and out:
            out[-1]["expression"] += "\n" + line
            continue
        m = re.match(r"^(.+?) =(?: (.*))?$", line)
        if not m:
            raise ValueError(f"measures.dax: cannot read line {line!r}")
        out.append({"name": m.group(1).strip(), "expression": (m.group(2) or "").strip(), "displayFolder": folder})
    for m in out:
        m["expression"] = m["expression"].strip()
        n = m["name"]
        if re.search(r"Colour|Label|Selected", n):
            continue
        if n.endswith("%") or "% (" in n or re.search(r"Rate|Utilisation|Coverage|Capacity Line", n):
            m["formatString"] = "0.0%"
        elif re.search(r"\bMM\b|MMonth|Heads", n):
            m["formatString"] = "#,0.0"
        else:
            m["formatString"] = "#,0"
    return out


# ------------------------------------------------------------------------------------------------ model
def tag(*parts) -> str:
    return str(uuid.uuid5(NS, "|".join(parts)))


def column(table: str, name: str, typ: str, hidden: bool, sort_by: str | None) -> dict:
    c = {"name": name, "dataType": TOM_TYPE[typ], "sourceColumn": name, "lineageTag": tag(table, name),
         "summarizeBy": "sum" if typ == N and re.search(r"MH|Value", name) else "none",
         "annotations": [{"name": "SummarizationSetBy", "value": "Automatic"}]}
    if typ == D:
        c["formatString"] = "mmm yyyy" if name == "Month" else "dd-mmm-yy"
        c["annotations"].append({"name": "UnderlyingDateTimeDataType", "value": "Date"})
    elif typ == N:
        c["formatString"] = "0.0%" if "Rate" in name else "#,0"
    elif typ == I:
        c["formatString"] = "0"
    if hidden:
        c["isHidden"] = True
    if sort_by:
        c["sortByColumn"] = sort_by
    return c


def table(name: str, cols: list[dict], m: str, measures=None, hidden=False) -> dict:
    t = {"name": name, "lineageTag": tag(name), "columns": cols,
         "partitions": [{"name": name, "mode": "import", "source": {"type": "m", "expression": m.split("\n")}}],
         "annotations": [{"name": "PBI_ResultType", "value": "Table"}]}
    if measures:
        t["measures"] = [dict(m_, lineageTag=tag(name, "measure", m_["name"])) for m_ in measures]
    if hidden:
        t["isHidden"] = True
    return t


def build_model(root: str, measures: list[dict]) -> dict:
    tables = []
    for name, spec in TABLES.items():
        sort = dict(spec.get("sort", []))
        cols = [column(name, c, t, c in spec.get("hidden", []), sort.get(c)) for c, t in spec["cols"]]
        tables.append(table(name, cols, m_table(name, spec)))
    tables.append(table("DimCycle", [column("DimCycle", "Cycle", T, False, None)], M_CYCLE))
    tables.append(table("_Measures", [column("_Measures", "Measures", T, True, None)], M_MEASURES, measures))
    rels = []
    for ft, fc, tt, tc, both in RELATIONSHIPS:
        r = {"name": tag("rel", ft, fc, tt, tc), "fromTable": ft, "fromColumn": fc, "toTable": tt, "toColumn": tc}
        if both:
            r["crossFilteringBehavior"] = "bothDirections"
        rels.append(r)
    order = ["OutputRoot", "fnLoadCsv"] + list(TABLES) + ["DimCycle", "_Measures"]
    return {
        "name": tag("model"), "compatibilityLevel": 1550,
        "model": {
            "culture": "en-US",
            "dataAccessOptions": {"legacyRedirects": True, "returnErrorValuesAsNull": True},
            "defaultPowerBIDataSourceVersion": "powerBI_V3",
            "sourceQueryCulture": "en-US",
            "tables": tables,
            "relationships": rels,
            "cultures": [{"name": "en-US", "linguisticMetadata": {
                "content": {"Version": "1.0.0", "Language": "en-US"}, "contentType": "json"}}],
            "expressions": [
                {"name": "OutputRoot", "kind": "m", "lineageTag": tag("OutputRoot"),
                 "expression": f'"{root}" meta [IsParameterQuery=true, Type="Text", IsParameterQueryRequired=true]',
                 "annotations": [{"name": "PBI_ResultType", "value": "Text"}]},
                {"name": "fnLoadCsv", "kind": "m", "lineageTag": tag("fnLoadCsv"), "expression": FN_LOAD_CSV.split("\n"),
                 "annotations": [{"name": "PBI_ResultType", "value": "Function"}]}],
            "annotations": [{"name": "PBI_QueryOrder", "value": json.dumps(order)},
                            {"name": "__PBI_TimeIntelligenceEnabled", "value": "0"},
                            {"name": "PBIDesktopVersion", "value": "2.128.751.0 (24.04)"}],
        }}


# ------------------------------------------------------------------------------------------------ report
def lit(v) -> dict:
    return {"expr": {"Literal": {"Value": v}}}


def field_m(name):            # measure in _Measures
    return ("m", "_Measures", name)


def field_c(tbl, col):        # column
    return ("c", tbl, col)


def field_s(tbl, col):        # sum of a column
    return ("s", tbl, col)


def qref(f) -> str:
    kind, tbl, prop = f
    return f"Sum({tbl}.{prop})" if kind == "s" else f"{tbl}.{prop}"


class Report:
    def __init__(self, logo: str | None):
        self.logo = logo
        self.sections = []
        self.n = 0

    def _id(self) -> str:
        self.n += 1
        return uuid.uuid5(NS, f"visual{self.n}").hex[:20]

    @staticmethod
    def _query(fields, order_by=None):
        alias, frm = {}, []
        for _, tbl, _ in fields:
            if tbl not in alias:
                a = re.sub(r"[^a-z]", "", tbl.lower())[:1] or "t"
                k = a
                i = 1
                while k in alias.values():
                    k, i = f"{a}{i}", i + 1
                alias[tbl] = k
                frm.append({"Name": k, "Entity": tbl, "Type": 0})

        def expr(f):
            kind, tbl, prop = f
            src = {"SourceRef": {"Source": alias[tbl]}}
            if kind == "m":
                return {"Measure": {"Expression": src, "Property": prop}}
            col = {"Column": {"Expression": src, "Property": prop}}
            return {"Aggregation": {"Expression": col, "Function": 0}} if kind == "s" else col
        sel, seen = [], set()
        for f in fields:
            if qref(f) not in seen:
                seen.add(qref(f))
                sel.append(dict(expr(f), Name=qref(f)))
        q = {"Version": 2, "From": frm, "Select": sel}
        if order_by:
            f, desc = order_by
            q["OrderBy"] = [{"Direction": 2 if desc else 1, "Expression": expr(f)}]
        return q

    def page(self, name: str):
        self.sections.append({"name": "ReportSection" + uuid.uuid5(NS, name).hex[:20], "displayName": name,
                              "filters": "[]", "ordinal": len(self.sections), "visualContainers": [],
                              "config": json.dumps({"objects": {}}), "displayOption": 1, "width": 1280, "height": 720})
        self._header(name)

    def add(self, vtype, x, y, w, h, roles=None, title=None, objects=None, order_by=None, filters=None, names=None,
            vc_extra=None):
        sec = self.sections[-1]
        z = len(sec["visualContainers"]) * 1000
        pos = {"x": x, "y": y, "z": z, "width": w, "height": h, "tabOrder": z}
        sv = {"visualType": vtype, "drillFilterOtherVisuals": True}
        if roles:
            fields = [f for fs in roles.values() for f in fs]
            sv["projections"] = {r: [dict({"queryRef": qref(f)}, **({"active": True} if r in ("Category", "Rows", "Columns") else {}))
                                     for f in fs] for r, fs in roles.items()}
            sv["prototypeQuery"] = self._query(fields, order_by)
        if names:
            sv["columnProperties"] = {qref(f): {"displayName": n} for f, n in names.items()}
        if objects:
            sv["objects"] = objects
        vco = {"title": [{"properties": {"show": lit("true" if title else "false"),
                                         **({"text": lit(f"'{title}'")} if title else {})}}]}
        if vc_extra:
            vco.update(vc_extra)
        sv["vcObjects"] = vco
        cfg = {"name": self._id(), "layouts": [{"id": 0, "position": pos}], "singleVisual": sv}
        sec["visualContainers"].append({"x": x, "y": y, "z": z, "width": w, "height": h, "config": json.dumps(cfg),
                                        "filters": json.dumps(filters or []), "tabOrder": z})

    # --- building blocks
    def text(self, x, y, w, h, txt, size="14pt", bold=True, align="left"):
        run = {"value": txt, "textStyle": {"fontSize": size, **({"fontWeight": "bold"} if bold else {}), "color": "#1F3864"}}
        self.add("textbox", x, y, w, h, objects={"general": [{"properties": {"paragraphs": [
            {"textRuns": [run], "horizontalTextAlignment": align}]}}]})

    def card(self, x, y, w, h, measure, label=None):
        self.add("card", x, y, w, h, {"Values": [field_m(measure)]},
                 objects={"labels": [{"properties": {"fontSize": lit("18D")}}],
                          "categoryLabels": [{"properties": {"show": lit("true"), "fontSize": lit("9D")}}]},
                 names={field_m(measure): label} if label else None)

    def slicer(self, x, y, w, h, f, mode="Dropdown", single=False, title=None):
        obj = {"data": [{"properties": {"mode": lit(f"'{mode}'")}}],
               "selection": [{"properties": {"strictSingleSelect": lit("true" if single else "false"),
                                             "selectAllCheckboxEnabled": lit("false" if single else "true")}}],
               "header": [{"properties": {"show": lit("true"), **({"text": lit(f"'{title}'")} if title else {})}}]}
        self.add("slicer", x, y, w, h, {"Values": [f]}, objects=obj)

    def _header(self, page_name):
        if self.logo:
            self.add("image", 0, 0, 150, 50, objects={"general": [{"properties": {"imageUrl": {"expr": {"ResourcePackageItem": {
                "PackageName": "RegisteredResources", "PackageType": 1, "ItemName": self.logo}}}}}]})
        else:
            self.text(0, 0, 150, 50, "ENPPI", size="20pt")
        self.text(155, 2, 560, 28, "Company Workload Forecast - Comments Analysis", size="15pt")
        self.text(155, 31, 560, 24, page_name, size="11pt", bold=False)
        self.slicer(720, 2, 135, 54, field_c("DimCycle", "Cycle"), single=True, title="Cycle")
        self.slicer(860, 2, 210, 54, field_c("DimDepartment", "Dept Label"), title="Department")
        self.slicer(1075, 2, 200, 54, field_c("DimCategory", "Category"), title="Category")


def in_filter(tbl: str, col: str, values: list[str]) -> dict:
    a = tbl[:1].lower()
    return {"name": "Filter" + uuid.uuid5(NS, f"{tbl}{col}{values}").hex[:20],
            "expression": {"Column": {"Expression": {"SourceRef": {"Entity": tbl}}, "Property": col}},
            "filter": {"Version": 2, "From": [{"Name": a, "Entity": tbl, "Type": 0}],
                       "Where": [{"Condition": {"In": {"Expressions": [{"Column": {"Expression": {"SourceRef": {"Source": a}},
                                                                                   "Property": col}}],
                                                       "Values": [[{"Literal": {"Value": f"'{v}'"}}] for v in values]}}}]},
            "type": "Categorical", "howCreated": 1}


def build_report(logo: str | None) -> Report:
    R = Report(logo)
    M, C, S = field_m, field_c, field_s
    dept, month = C("DimDepartment", "Dept Label"), C("DimDate", "Month")

    R.page("Comments Overview")
    cards = [("Planning MH", "Planning Input MH"), ("Discipline MH", "Discipline MH"), ("Implemented MH", "After Impl. MH"),
             ("Disc vs Plan MH", "Net deviation MH"), ("Disc vs Plan %", "Net deviation %"),
             ("Implementation Rate", "Implementation rate"), ("Concern Items", "Concern items"),
             ("Feedback Coverage %", "Feedback coverage")]
    for k, (m, lab) in enumerate(cards):
        R.card(5 + k * 159, 62, 154, 72, m, lab)
    R.add("lineChart", 5, 140, 760, 330, {"Category": [month], "Y": [M("Planning MH"), M("Discipline MH"),
                                                                       M("Implemented MH"), M("Capacity MH")]},
          title="Company workload by month - three scenarios vs capacity (MH)")
    R.add("clusteredColumnChart", 5, 475, 760, 240, {"Y": [M("New Work MH"), M("Increase Within Schedule MH"),
                                                           M("Extension or Earlier Start MH"), M("Reduction Within Schedule MH"),
                                                           M("Removed Allocation MH")]},
          title="Net deviation bridge (MH): new work, increase, extension, reduction, removed")
    R.add("clusteredBarChart", 770, 140, 505, 575, {"Category": [dept], "Y": [M("Disc vs Plan MH")]},
          title="Net deviation by department (Discipline - Planning, MH)", order_by=(M("Disc vs Plan MH"), True))

    R.page("Workload Comparison - MHRs")
    R.add("tableEx", 0, 62, 370, 300, {"Values": [dept, M("ABS_Variance")]}, title="Difference by Department",
          order_by=(M("ABS_Variance"), True), names={M("ABS_Variance"): "Abs. difference MH"})
    R.add("tableEx", 0, 367, 370, 348, {"Values": [C("DimProject", "Project Label"), M("ABS_Variance")]},
          title="Difference by Project", order_by=(M("ABS_Variance"), True), names={M("ABS_Variance"): "Abs. difference MH"})
    R.add("lineChart", 375, 62, 900, 395, {"Category": [month], "Y": [M("Planning MH"), M("Discipline MH"), M("Implemented MH")],
                                           "Tooltips": [M("Discipline MM"), M("ABS_Variance_MMonth")]},
          title="Planning Input vs Discipline Comment vs After Implementation (MH)")
    R.add("clusteredColumnChart", 375, 462, 900, 253, {"Category": [month], "Y": [M("Disc vs Plan MH"), M("Impl vs Disc MH")]},
          title="Monthly deviation (MH): Discipline - Planning, After Impl. - Discipline")

    R.page("Department Comparison")
    R.add("pivotTable", 5, 62, 1270, 340, {"Rows": [dept], "Values": [
        M("Planning MH"), M("Discipline MH"), M("Implemented MH"), M("Disc vs Plan MH"), M("Disc vs Plan %"),
        M("Concern Items"), M("Implementation Rate"), M("Utilisation Discipline"), M("Firm Utilisation Discipline"),
        M("Overload Months (Discipline)")]}, title="Department comparison",
          names={C("DimDepartment", "Dept Label"): "Department"})
    R.add("pivotTable", 5, 407, 1270, 308, {"Rows": [dept], "Columns": [C("DimDate", "Month Label")],
                                            "Values": [M("Utilisation Discipline")]},
          title="Utilisation by month - discipline-adjusted MH / capacity")

    R.page("Project Concerns")
    R.slicer(5, 62, 180, 160, C("DimProject", "Severity"), mode="Basic", title="Severity")
    R.slicer(5, 227, 180, 300, C("BridgeConcernType", "Brief Category"), mode="Basic", title="Concern category")
    R.slicer(5, 532, 180, 183, C("DimProject", "Master Flag"), mode="Basic", title="Project master check")
    R.add("tableEx", 190, 62, 1085, 400, {"Values": [
        C("DimProject", "Project Label"), C("DimProject", "Severity"), C("DimProject", "Severity Points"),
        C("DimProject", "Depts Concerned"), C("DimProject", "Departments Raising"), M("Planning MH"),
        M("Discipline MH"), M("Disc vs Plan MH"), M("Implementation Rate"), C("DimProject", "Master Flag")]},
          title="Projects ranked by severity", order_by=(C("DimProject", "Severity Points"), True))
    R.add("pivotTable", 190, 467, 1085, 248, {"Rows": [C("DimProject", "Project Label")], "Columns": [dept],
                                              "Values": [M("Disc vs Plan MH")]}, title="Deviation by project and department (MH)")

    R.page("Added Projects")
    R.card(5, 62, 200, 72, "New Project MH", "New allocations MH")
    R.card(210, 62, 200, 72, "New Project Rows", "New allocation rows")
    new_types = ["New Project", "New Phase Allocation", "New Project (code reused - verify)"]
    R.add("pivotTable", 5, 140, 1270, 575, {"Rows": [C("FactCommentItems", "DeptCode"), C("FactCommentItems", "ProjectCode"),
                                                     C("FactCommentItems", "Activity")],
                                            "Columns": [C("DimDate", "Month Label")],
                                            "Values": [S("FactCellChanges", "Cell Discipline MH")]},
          title="New allocations requested by departments (MH by month)",
          filters=[in_filter("FactCommentItems", "Primary Type", new_types)],
          names={S("FactCellChanges", "Cell Discipline MH"): "Requested MH"})

    R.page("Implementation Tracking")
    R.add("barChart", 5, 62, 620, 653, {"Category": [dept], "Y": [M("Items Implemented"), M("Items Partially Implemented"),
                                                                  M("Items Not Implemented")]},
          title="Concern items by implementation status")
    R.card(630, 62, 210, 72, "Implementation Rate", "Implementation rate")
    R.card(845, 62, 210, 72, "Implemented of Request MH", "Implemented of request MH")
    R.card(1060, 62, 215, 72, "Planner Change Without Comment MH", "Changed without comment MH")
    R.add("tableEx", 630, 140, 645, 575, {"Values": [
        C("FactCommentItems", "DeptCode"), C("FactCommentItems", "ProjectCode"), C("FactCommentItems", "Activity"),
        S("FactCommentItems", "Item Planning MH"), S("FactCommentItems", "Item Discipline MH"),
        S("FactCommentItems", "Item Implemented MH"), C("FactCommentItems", "Concern Types"),
        C("FactCommentItems", "Implementation Status")]},
          title="Requests not implemented", order_by=(S("FactCommentItems", "Item Discipline MH"), True),
          filters=[in_filter("FactCommentItems", "Implementation Status",
                             ["Not implemented", "Not implemented (row absent after implementation)"])])

    R.page("Capacity & Overload")
    R.add("lineChart", 5, 62, 800, 335, {"Category": [month], "Y": [M("Utilisation Planning"), M("Utilisation Discipline"),
                                                                     M("Utilisation Implemented"), M("Capacity Line")]},
          title="Utilisation by month (100% = capacity)")
    R.card(810, 62, 150, 72, "Extra Heads Needed (Peak)", "Extra heads at peak")
    R.card(965, 62, 150, 72, "Overload Months (Discipline)", "Overload months")
    R.card(1120, 62, 155, 72, "Firm Utilisation Discipline", "Firm utilisation")
    R.add("tableEx", 810, 140, 465, 257, {"Values": [dept, M("Running+Loan% (Discipline)"), M("Internal% (Discipline)"),
                                                     M("Signature% (Discipline)"), M("Proposal% (Discipline)"),
                                                     M("Potential% (Discipline)"), M("WL% (Discipline)")]},
          title="Workload % of capacity by category (discipline-adjusted, as in the workload report)")
    R.add("pivotTable", 5, 402, 1270, 313, {"Rows": [dept], "Columns": [C("DimDate", "Month Label")],
                                            "Values": [M("Utilisation Discipline")]}, title="Department utilisation by month")

    R.page("Cell Detail")
    R.slicer(5, 62, 260, 653, C("FactCommentItems", "ProjectCode"), mode="Basic", title="Project")
    R.add("tableEx", 270, 62, 1005, 653, {"Values": [
        C("FactCellChanges", "DeptCode"), C("FactCellChanges", "ProjectCode"), C("FactCellChanges", "Month"),
        S("FactCellChanges", "Cell Planning MH"), S("FactCellChanges", "Discipline Value"), S("FactCellChanges", "Cell Discipline MH"),
        S("FactCellChanges", "Change MH"), S("FactCellChanges", "Cell Implemented MH"), C("FactCellChanges", "Cell Status"),
        C("FactCellChanges", "Blank Note")]}, title="Month-level changes and retained blanks")

    R.page("Data Quality")
    R.slicer(5, 62, 200, 300, C("DataQuality", "Area"), mode="Basic", title="Area")
    R.slicer(5, 367, 200, 150, C("DataQuality", "Severity"), mode="Basic", title="Severity")
    R.add("tableEx", 210, 62, 1065, 330, {"Values": [
        C("DataQuality", "Severity"), C("DataQuality", "Area"), C("DataQuality", "File"), C("DataQuality", "Issue"),
        C("DataQuality", "Detail"), S("DataQuality", "Impact_MH")]}, title="Issues and corrections")
    R.add("tableEx", 210, 397, 640, 318, {"Values": [
        C("FileInventory", "Dept"), C("FileInventory", "Scenario"), C("FileInventory", "File Used"),
        C("FileInventory", "Comments Received"), C("FileInventory", "Action Needed")]}, title="File inventory")
    R.add("tableEx", 855, 397, 420, 318, {"Values": [
        C("PossibleTransfers", "Dept"), C("PossibleTransfers", "Reduced Project"), C("PossibleTransfers", "Added Project"),
        S("PossibleTransfers", "Possible Offset MH")]}, title="Possible transfers (counted twice in gross)")
    return R


def layout(report: Report, theme_name: str, base: tuple[str, str] | None) -> dict:
    items = [{"type": 202, "path": theme_name, "name": theme_name}]
    if report.logo:
        items.append({"type": 100, "path": report.logo, "name": report.logo})
    packages = [{"resourcePackage": {"name": "RegisteredResources", "type": 1, "items": items, "disabled": False}}]
    theme = {"customTheme": {"name": theme_name, "version": "5.37", "type": 1}}
    if base:
        packages.insert(0, {"resourcePackage": {"name": "SharedResources", "type": 2, "disabled": False,
                                                "items": [{"type": 202, "path": f"BaseThemes/{base[0]}.json", "name": base[0]}]}})
        theme["baseTheme"] = {"name": base[0], "version": base[1], "type": 2}
    config = {"version": "5.37", "themeCollection": theme, "activeSectionIndex": 0, "defaultDrillFilterOtherVisuals": True,
              "settings": {"useStylableVisualContainerHeader": True, "exportDataMode": 1, "useNewFilterPaneExperience": True,
                           "allowChangeFilterTypes": True, "useEnhancedTooltips": True},
              "objects": {"outspacePane": [{"properties": {"expanded": lit("false")}}]}}
    return {"id": 0, "resourcePackages": packages, "sections": report.sections, "config": json.dumps(config),
            "layoutOptimization": 0, "filters": "[]", "publicCustomVisuals": []}


def diagram() -> dict:
    names = list(TABLES) + ["DimCycle", "_Measures"]
    nodes = [{"location": {"x": 40 + (k % 5) * 280, "y": 40 + (k // 5) * 340}, "nodeIndex": n,
              "size": {"height": 300, "width": 234}, "zIndex": k} for k, n in enumerate(names)]
    return {"version": "1.1.0", "diagrams": [{"ordinal": 0, "scrollPosition": {"x": 0, "y": 0}, "nodes": nodes,
                                              "name": "All tables", "zoomValue": 80, "pinKeyFieldsToTop": False,
                                              "showExtraHeaderInfo": False, "hideKeyFieldsWhenCollapsed": False,
                                              "tablesLocked": False}],
            "selectedDiagram": "All tables", "defaultDiagram": "All tables"}


def u16(obj) -> bytes:
    return (obj if isinstance(obj, str) else json.dumps(obj, ensure_ascii=False)).encode("utf-16-le")


def build(out: Path, pbix: Path | None, root: str):
    measures = read_measures(HERE / "measures.dax")
    model = build_model(root, measures)
    logo, base, base_json, logo_bytes = None, None, None, None
    if pbix and pbix.exists():
        with zipfile.ZipFile(pbix) as z:
            for n in z.namelist():
                if n.startswith("Report/StaticResources/RegisteredResources/") and n.lower().endswith(".png") and "enppi" in n.lower():
                    logo, logo_bytes = n.rsplit("/", 1)[1], z.read(n)
            # reuse the base theme the workload report was built on (name and version from its layout)
            b = json.loads(json.loads(z.read("Report/Layout").decode("utf-16-le"))["config"]).get("themeCollection", {}).get("baseTheme")
            path = f"Report/StaticResources/SharedResources/BaseThemes/{b['name']}.json" if b else ""
            if path in z.namelist():
                base, base_json = (b["name"], b["version"]), z.read(path)
    report = build_report(logo)
    theme_name = "ENPPI_Workload_Theme.json"
    files = {
        "Version": "1.28".encode("utf-16-le"),
        "DataModelSchema": u16(model),
        "DiagramLayout": u16(diagram()),
        "Report/Layout": u16(layout(report, theme_name, base)),
        "Settings": u16({"Version": 2, "ReportSettings": {"ShowHiddenFields": False, "IsQnaEnabledForThisFile": False},
                         "QueriesSettings": {"TypeDetectionEnabled": True, "RelationshipImportEnabled": False}}),
        "Metadata": u16({"Version": 5, "AutoCreatedRelationships": [], "CreatedFrom": "OnPrem", "CreatedFromRelease": "2024.05"}),
        f"Report/StaticResources/RegisteredResources/{theme_name}": (HERE / "ENPPI_Workload_Theme.json").read_bytes(),
    }
    if logo:
        files[f"Report/StaticResources/RegisteredResources/{logo}"] = logo_bytes
    if base_json:
        files[f"Report/StaticResources/SharedResources/BaseThemes/{base[0]}.json"] = base_json
    ct = ('\ufeff<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
          '<Default Extension="json" ContentType="" /><Default Extension="png" ContentType="" />'
          + "".join(f'<Override PartName="/{p}" ContentType="" />' for p in
                    ("Version", "DataModelSchema", "DiagramLayout", "Report/Layout", "Settings", "Metadata"))
          + "</Types>")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("[Content_Types].xml", ct.encode("utf-8"))
        for name, data in files.items():
            z.writestr(name, data)
    return model, report


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default="Workload_Comments_Analysis.pbit")
    ap.add_argument("--pbix", help="existing workload report, to reuse the ENPPI logo and base theme")
    ap.add_argument("--root", default=r"C:\Workload Forecast\Comments Analysis\output",
                    help="default OutputRoot shown when the template opens")
    ap.add_argument("--queries", default=str(HERE / "queries.pq"), help="where to regenerate the Power Query file")
    a = ap.parse_args()
    model, report = build(Path(a.out), Path(a.pbix) if a.pbix else None, a.root)
    write_queries(Path(a.queries), a.root)
    n_vis = sum(len(s["visualContainers"]) for s in report.sections)
    print(f"{a.out}: {len(model['model']['tables'])} tables, {len(model['model']['relationships'])} relationships, "
          f"{len(model['model']['tables'][-1].get('measures', []))} measures, {len(report.sections)} pages, {n_vis} visuals")


if __name__ == "__main__":
    main()
