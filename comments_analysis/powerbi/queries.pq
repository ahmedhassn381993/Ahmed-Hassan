// =====================================================================================
// Workload Comments Analysis - Power Query (M)
// Paste each query into Power BI Desktop: Transform data > New Source > Blank Query >
// Advanced Editor. Create the parameter first.
//
// Folder layout expected (one sub-folder per forecast cycle, written by run_analysis.py):
//   <OutputRoot>\Q3_2026\powerbi\FactWorkload.csv ...
//   <OutputRoot>\Q4_2026\powerbi\FactWorkload.csv ...
// Every cycle found under OutputRoot is loaded, so cycle-to-cycle comparison works.
// All codes are loaded as TEXT: project codes mix 7-digit, 8-digit and suffixed forms
// (5305200, 40061420, 5020250-7) and must never be converted to numbers.
// =====================================================================================

// ---- Parameter: OutputRoot (Manage Parameters > New, Type = Text) -------------------
// "<shared folder path>\Comments Analysis\output"   meta [IsParameterQuery=true, Type="Text", IsParameterQueryRequired=true]

// ---- Function: fnLoadCsv -------------------------------------------------------------
(fileName as text, columnTypes as list) as table =>
let
    Files    = Folder.Files(OutputRoot),
    Picked   = Table.SelectRows(Files, each [Name] = fileName and not Text.StartsWith([Name], "~")),
    Parsed   = Table.AddColumn(Picked, "Data", each
                   Table.PromoteHeaders(
                       Csv.Document([Content], [Delimiter = ",", Encoding = 65001, QuoteStyle = QuoteStyle.Csv]),
                       [PromoteAllScalars = true])),
    Combined = Table.Combine(Parsed[Data]),
    Typed    = Table.TransformColumnTypes(Combined, columnTypes, "en-US")
in
    Typed

// ---- FactWorkload (one row per cycle / scenario / department / project-phase / month) -
let
    Source = fnLoadCsv("FactWorkload.csv", {
        {"Cycle", type text}, {"Scenario", type text}, {"DeptCode", Int64.Type}, {"Category", type text},
        {"ProjectCode", type text}, {"RowKey", type text}, {"Month", type date}, {"MH", type number}})
in
    Source

// ---- FactCommentItems (one row per department / sub-dept / phase / project) -----------
let
    Source = fnLoadCsv("FactCommentItems.csv", {
        {"Cycle", type text}, {"RowKey", type text}, {"DeptCode", Int64.Type}, {"SubDept", type text},
        {"Category", type text}, {"ProjectCode", type text}, {"Activity", type text},
        {"Planning MH", type number}, {"Discipline MH", type number}, {"Implemented MH", type number},
        {"Gross Increase", type number}, {"Gross Reduction", type number}, {"Net Change", type number},
        {"Concern", type text}, {"Primary Type", type text}, {"Concern Types", type text},
        {"Implementation Status", type text}, {"Implementation Rate", type number},
        {"Implemented Request MH", type number}, {"Unrequested Change MH", type number},
        {"Requested Phase", type text}, {"Code Correction", type text}, {"Months Changed", type text},
        {"Blank Not Applied MH", type number}, {"Feedback File", type text}, {"Feedback Row", Int64.Type},
        {"CycleRowKey", type text}})
in
    Source

// ---- FactCellChanges (month-level changes and unresolved blanks) ----------------------
let
    Source = fnLoadCsv("FactCellChanges.csv", {
        {"Cycle", type text}, {"RowKey", type text}, {"DeptCode", Int64.Type}, {"ProjectCode", type text}, {"Month", type date},
        {"Planning MH", type number}, {"Discipline Value", type number}, {"Discipline MH", type number},
        {"Change MH", type number}, {"Implemented MH", type number}, {"Cell Status", type text}, {"Blank Note", type text},
        {"CycleRowKey", type text}})
in
    Source

// ---- BridgeConcernType ----------------------------------------------------------------
let
    Source = fnLoadCsv("BridgeConcernType.csv", {{"Cycle", type text}, {"RowKey", type text}, {"CycleRowKey", type text},
        {"ConcernType", type text}})
in
    Source

// ---- DimDepartment --------------------------------------------------------------------
let
    Source  = fnLoadCsv("DimDepartment.csv", {
        {"DeptCode", Int64.Type}, {"Department", type text}, {"Division", Int64.Type}, {"Service", type text},
        {"Dept Label", type text}, {"Managers", Int64.Type}, {"LoanTo_Unpaid", Int64.Type}, {"Permanent", Int64.Type},
        {"Temporary", Int64.Type}, {"Total", Int64.Type}, {"Effective_Heads", Int64.Type}, {"Capacity_MH_per_Month", type number}}),
    Latest  = Table.Distinct(Source, {"DeptCode"})
in
    Latest

// ---- DimProject -----------------------------------------------------------------------
let
    Source = fnLoadCsv("DimProject.csv", {
        {"ProjectCode", type text}, {"Project Name", type text}, {"Base Code", type text}, {"Client", type text},
        {"Project Label", type text}, {"Severity", type text}, {"Severity Points", Int64.Type},
        {"Depts Concerned", Int64.Type}, {"Departments Raising", type text}, {"Severity Order", Int64.Type}}),
    Latest = Table.Distinct(Source, {"ProjectCode"})
in
    Latest

// ---- DimCategory / DimScenario / DimDate / DataQuality ----------------------------------
let Source = Table.Distinct(fnLoadCsv("DimCategory.csv", {{"Category", type text}, {"Category Order", Int64.Type},
        {"Parent Category", type text}, {"Firm Workload", type text}, {"Color", type text}}), {"Category"}) in Source

let Source = Table.Distinct(fnLoadCsv("DimScenario.csv", {{"Scenario", type text}, {"Scenario Order", Int64.Type},
        {"Color", type text}, {"Meaning", type text}}), {"Scenario"}) in Source

let Source = Table.Distinct(fnLoadCsv("DimDate.csv", {{"Month", type date}, {"Year", Int64.Type}, {"Quarter", type text},
        {"Month Label", type text}, {"Month Index", Int64.Type}, {"Period", type text}}), {"Month"}) in Source

let Source = fnLoadCsv("DataQuality.csv", {{"Severity", type text}, {"Area", type text}, {"Scenario", type text},
        {"File", type text}, {"Issue", type text}, {"Detail", type text}, {"Impact_MH", type number}}) in Source
