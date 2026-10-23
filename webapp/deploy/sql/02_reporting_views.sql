/* Reporting views for Power BI (read by a Power BI service identity with SELECT on schema reporting only).
   vwForecastScenarios has the same columns as FactWorkload.csv, so the Workload Comments Analysis report can move
   from the CSV export to Azure SQL by changing its source query, not its model. Run after 01_schema.sql. */
IF SCHEMA_ID(N'reporting') IS NULL EXEC (N'CREATE SCHEMA reporting');
GO

/* display label of a stored category (the names DimCategory and the CSV export use) */
CREATE OR ALTER FUNCTION reporting.CategoryLabel (@c nvarchar(20)) RETURNS nvarchar(40) WITH SCHEMABINDING AS
BEGIN
    RETURN CASE @c WHEN N'Running' THEN N'Running' WHEN N'Internal' THEN N'Internal / Unforeseen' WHEN N'LoanTo' THEN N'Loan To'
                   WHEN N'UnderSignature' THEN N'Under Signature' WHEN N'Proposal' THEN N'Proposal'
                   WHEN N'Potential' THEN N'Potential Opportunity' ELSE @c END;
END;
GO

/* one row per line and month of the cycle, with the stored values side by side - the same rules as the application
   (WorkloadService.BuildRows): every submitted request counts in the discipline scenario whatever its decision; a line that
   carries a new-allocation request takes the request as its discipline value; Planning Input and the discipline scenario
   keep the planner's category (a requested move is paired with its baseline row, as in the comments analysis), the final
   and after scenarios use the approved / re-extracted one */
CREATE OR ALTER VIEW reporting.vwLineMonths AS
WITH n AS (SELECT TOP (60) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS MonthIndex FROM sys.all_objects)
SELECT  l.Id AS LineId, l.CycleId, c.Name AS Cycle, l.DeptCode, l.SectionCode,
        reporting.CategoryLabel(l.Category) AS Category,
        reporting.CategoryLabel(l.Category) AS DisciplineCategory, reporting.CategoryLabel(mv.RequestedCategory) AS RequestedCategory,
        reporting.CategoryLabel(COALESCE(l.ApprovedCategory, l.Category)) AS FinalCategory,
        reporting.CategoryLabel(CASE WHEN c.HasAfterData = 1 THEN COALESCE(l.AfterCategory, l.Category)
                                     ELSE COALESCE(l.ApprovedCategory, l.Category) END) AS AfterCategory,
        l.ProjectCode, l.Activity, l.PlannerUpn, l.Origin, n.MonthIndex, DATEADD(month, n.MonthIndex, c.FirstMonth) AS [Month],
        ISNULL(v.PlannerMH, 0) AS PlanningMH,
        /* blank proposal = planner value kept */
        CASE WHEN np.Id IS NOT NULL THEN ISNULL(nv.ProposedMH, 0) ELSE COALESCE(pv.ProposedMH, v.PlannerMH, 0) END AS DisciplineMH,
        COALESCE(v.ApprovedMH, v.PlannerMH, 0) AS FinalMH,
        CASE WHEN c.HasAfterData = 1 THEN ISNULL(v.AfterMH, 0) ELSE COALESCE(v.ApprovedMH, v.PlannerMH, 0) END AS AfterMH
FROM dbo.Lines l
JOIN dbo.Cycles c ON c.Id = l.CycleId
JOIN n ON n.MonthIndex < c.MonthCount
LEFT JOIN dbo.[Values] v ON v.LineId = l.Id AND v.MonthIndex = n.MonthIndex
LEFT JOIN dbo.Proposals p ON p.LineId = l.Id AND p.Kind = N'Change' AND p.Status <> N'Draft'
LEFT JOIN dbo.ProposalValues pv ON pv.ProposalId = p.Id AND pv.MonthIndex = n.MonthIndex
LEFT JOIN dbo.Proposals np ON np.LineId = l.Id AND np.Kind = N'NewAllocation' AND np.Status <> N'Draft'
LEFT JOIN dbo.ProposalValues nv ON nv.ProposalId = np.Id AND nv.MonthIndex = n.MonthIndex
LEFT JOIN dbo.Proposals mv ON mv.LineId = l.Id AND mv.Kind = N'CategoryChange' AND mv.Status <> N'Draft';
GO

/* the three report scenarios in the FactWorkload.csv layout (same RowKey); new-allocation requests not yet on a line count
   as discipline values */
CREATE OR ALTER VIEW reporting.vwForecastScenarios AS
SELECT Cycle, N'Planning Input' AS Scenario, DeptCode, Category, ProjectCode,
       CONCAT(DeptCode, N'|', SectionCode, N'|', Category, N'|L', LineId) AS RowKey, [Month], CAST(PlanningMH AS float) AS MH
FROM reporting.vwLineMonths WHERE PlanningMH <> 0
UNION ALL
SELECT Cycle, N'Discipline Comment', DeptCode, DisciplineCategory, ProjectCode, CONCAT(DeptCode, N'|', SectionCode, N'|', Category, N'|L', LineId),
       [Month], CAST(DisciplineMH AS float)
FROM reporting.vwLineMonths WHERE DisciplineMH <> 0
UNION ALL
SELECT Cycle, N'After Implementation', DeptCode, AfterCategory, ProjectCode, CONCAT(DeptCode, N'|', SectionCode, N'|', Category, N'|L', LineId),
       [Month], CAST(AfterMH AS float)
FROM reporting.vwLineMonths WHERE AfterMH <> 0
UNION ALL
SELECT c.Name, N'Discipline Comment', p.DeptCode, reporting.CategoryLabel(ISNULL(p.NewCategory, N'Proposal')), p.NewProjectCode,
       CONCAT(p.DeptCode, N'|', ISNULL(p.NewSectionCode, p.DeptCode), N'|', reporting.CategoryLabel(ISNULL(p.NewCategory, N'Proposal')), N'|P', p.Id),
       DATEADD(month, pv.MonthIndex, c.FirstMonth), CAST(pv.ProposedMH AS float)
FROM dbo.Proposals p
JOIN dbo.Cycles c ON c.Id = p.CycleId
JOIN dbo.ProposalValues pv ON pv.ProposalId = p.Id
WHERE p.Kind = N'NewAllocation' AND p.LineId IS NULL AND p.Status <> N'Draft' AND ISNULL(pv.ProposedMH, 0) <> 0
      AND pv.MonthIndex < c.MonthCount;
GO

/* department x month workload against capacity (ROUNDUP(permanent x effectiveness) + temporary) x MH per man-month, with
   the factors of Administration > Settings (defaults 0.9 and 150) - or, for a locked cycle, the headcount and factors it
   was locked with */
CREATE OR ALTER VIEW reporting.vwDisciplineWorkload AS
WITH s AS (
    SELECT ISNULL(MAX(CASE WHEN [Key] = N'PermanentEffectiveness' THEN TRY_CAST([Value] AS float) END), 0.9) AS Eff,
           ISNULL(MAX(CASE WHEN [Key] = N'HoursPerManMonth' THEN TRY_CAST([Value] AS float) END), 150) AS Hours
    FROM dbo.Settings),
m AS (
    SELECT CycleId, Cycle, DeptCode, [Month], SUM(PlanningMH) AS PlanningMH, SUM(DisciplineMH) AS DisciplineMH, SUM(FinalMH) AS FinalMH,
           SUM(AfterMH) AS AfterMH
    FROM reporting.vwLineMonths GROUP BY CycleId, Cycle, DeptCode, [Month])
SELECT m.Cycle, m.DeptCode, ISNULL(d.Name, N'(not in the department master)') AS Department, m.[Month],
       m.PlanningMH, m.DisciplineMH, m.FinalMH, m.AfterMH,
       CASE WHEN c.LockedCapacity IS NOT NULL
            THEN (CEILING(ISNULL(TRY_CAST(JSON_VALUE(c.LockedCapacity, CONCAT(N'$.Heads."', m.DeptCode, N'"[0]')) AS float), 0) *
                          ISNULL(TRY_CAST(JSON_VALUE(c.LockedCapacity, N'$.Settings.PermanentEffectiveness') AS float), s.Eff)) +
                  ISNULL(TRY_CAST(JSON_VALUE(c.LockedCapacity, CONCAT(N'$.Heads."', m.DeptCode, N'"[1]')) AS float), 0)) *
                 ISNULL(TRY_CAST(JSON_VALUE(c.LockedCapacity, N'$.Settings.HoursPerManMonth') AS float), s.Hours)
            ELSE (CEILING(ISNULL(d.Permanent, 0) * s.Eff) + ISNULL(d.Temporary, 0)) * s.Hours END AS CapacityMH
FROM m
JOIN dbo.Cycles c ON c.Id = m.CycleId
CROSS JOIN s
LEFT JOIN dbo.Departments d ON d.Code = m.DeptCode;
GO

/* every department request with its decision and reason */
CREATE OR ALTER VIEW reporting.vwCommentItems AS
SELECT c.Name AS Cycle, p.Id AS ProposalId, p.DeptCode, COALESCE(l.ProjectCode, p.NewProjectCode) AS ProjectCode,
       COALESCE(l.Activity, p.NewActivity) AS Activity, reporting.CategoryLabel(COALESCE(l.Category, p.NewCategory)) AS Category, p.Kind, p.Status,
       p.ChangeTypes, reporting.CategoryLabel(p.RequestedCategory) AS RequestedCategory, p.Reason, p.CreatedBy, p.SubmittedAt, p.ReviewedBy, p.ReviewedAt, p.ReviewComment,
       p.ImplementedBy, p.ImplementedAt
FROM dbo.Proposals p
JOIN dbo.Cycles c ON c.Id = p.CycleId
LEFT JOIN dbo.Lines l ON l.Id = p.LineId
WHERE p.Status <> N'Draft';
GO

CREATE OR ALTER VIEW reporting.vwPendingRevisions AS
SELECT * FROM reporting.vwCommentItems WHERE Status = N'Submitted';
GO

CREATE OR ALTER VIEW reporting.vwChangeHistory AS
SELECT a.At, a.[User], a.Action, a.Entity, a.EntityKey, c.Name AS Cycle, a.Details
FROM dbo.Audit a LEFT JOIN dbo.Cycles c ON c.Id = a.CycleId;
GO

/* Power BI service identity (create the Entra principal first; name is an example):
   CREATE USER [enppi-workload-powerbi] FROM EXTERNAL PROVIDER;
   GRANT SELECT ON SCHEMA::reporting TO [enppi-workload-powerbi]; */
