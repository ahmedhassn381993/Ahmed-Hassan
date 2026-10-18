/* Reporting views for Power BI (read by a Power BI service identity with SELECT on schema reporting only).
   vwForecastScenarios has the same columns as FactWorkload.csv, so the Workload Comments Analysis report can move
   from the CSV export to Azure SQL by changing its source query, not its model. Run after 01_schema.sql. */
IF SCHEMA_ID(N'reporting') IS NULL EXEC (N'CREATE SCHEMA reporting');
GO

/* one row per line and month of the cycle, with the four stored values side by side */
CREATE OR ALTER VIEW reporting.vwLineMonths AS
WITH n AS (SELECT TOP (60) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS MonthIndex FROM sys.all_objects)
SELECT  l.Id AS LineId, l.CycleId, c.Name AS Cycle, l.DeptCode, l.SectionCode,
        CASE l.Category WHEN N'Internal' THEN N'Internal / Unforeseen' WHEN N'LoanTo' THEN N'Loan To'
                        WHEN N'UnderSignature' THEN N'Under Signature' WHEN N'Potential' THEN N'Potential Opportunity'
                        ELSE l.Category END AS Category,
        l.ProjectCode, l.Activity, l.PlannerUpn, n.MonthIndex, DATEADD(month, n.MonthIndex, c.FirstMonth) AS [Month],
        ISNULL(v.PlannerMH, 0) AS PlanningMH,
        /* blank proposal = planner value kept; a line created from a new-allocation request has no planner value */
        COALESCE(pv.ProposedMH, nv.ProposedMH, v.PlannerMH, 0) AS DisciplineMH,
        COALESCE(v.ApprovedMH, v.PlannerMH, 0) AS FinalMH,
        CASE WHEN c.HasAfterData = 1 THEN ISNULL(v.AfterMH, 0) ELSE COALESCE(v.ApprovedMH, v.PlannerMH, 0) END AS AfterMH
FROM dbo.Lines l
JOIN dbo.Cycles c ON c.Id = l.CycleId
JOIN n ON n.MonthIndex < c.MonthCount
LEFT JOIN dbo.[Values] v ON v.LineId = l.Id AND v.MonthIndex = n.MonthIndex
LEFT JOIN dbo.Proposals p ON p.LineId = l.Id AND p.Kind = N'Change' AND p.Status <> N'Draft'
LEFT JOIN dbo.ProposalValues pv ON pv.ProposalId = p.Id AND pv.MonthIndex = n.MonthIndex
LEFT JOIN dbo.Proposals np ON np.LineId = l.Id AND np.Kind = N'NewAllocation' AND np.Status <> N'Draft'
LEFT JOIN dbo.ProposalValues nv ON nv.ProposalId = np.Id AND nv.MonthIndex = n.MonthIndex;
GO

/* the three report scenarios in the FactWorkload.csv layout; pending new-allocation requests count as discipline values */
CREATE OR ALTER VIEW reporting.vwForecastScenarios AS
SELECT Cycle, N'Planning Input' AS Scenario, DeptCode, Category, ProjectCode,
       CONCAT(DeptCode, N'|', SectionCode, N'|', Category, N'|L', LineId) AS RowKey, [Month], CAST(PlanningMH AS float) AS MH
FROM reporting.vwLineMonths WHERE PlanningMH <> 0
UNION ALL
SELECT Cycle, N'Discipline Comment', DeptCode, Category, ProjectCode, CONCAT(DeptCode, N'|', SectionCode, N'|', Category, N'|L', LineId),
       [Month], CAST(DisciplineMH AS float)
FROM reporting.vwLineMonths WHERE DisciplineMH <> 0
UNION ALL
SELECT Cycle, N'After Implementation', DeptCode, Category, ProjectCode, CONCAT(DeptCode, N'|', SectionCode, N'|', Category, N'|L', LineId),
       [Month], CAST(AfterMH AS float)
FROM reporting.vwLineMonths WHERE AfterMH <> 0
UNION ALL
SELECT c.Name, N'Discipline Comment', p.DeptCode,
       CASE p.NewCategory WHEN N'UnderSignature' THEN N'Under Signature' WHEN N'Potential' THEN N'Potential Opportunity' ELSE p.NewCategory END,
       p.NewProjectCode, CONCAT(p.DeptCode, N'|', ISNULL(p.NewSectionCode, p.DeptCode), N'|P', p.Id),
       DATEADD(month, pv.MonthIndex, c.FirstMonth), CAST(pv.ProposedMH AS float)
FROM dbo.Proposals p
JOIN dbo.Cycles c ON c.Id = p.CycleId
JOIN dbo.ProposalValues pv ON pv.ProposalId = p.Id
WHERE p.Kind = N'NewAllocation' AND p.LineId IS NULL AND p.Status NOT IN (N'Draft', N'Rejected') AND ISNULL(pv.ProposedMH, 0) <> 0;
GO

/* department x month workload against capacity (ROUNDUP(permanent x 0.9) + temporary) x 150 MH */
CREATE OR ALTER VIEW reporting.vwDisciplineWorkload AS
SELECT m.Cycle, m.DeptCode, d.Name AS Department, m.[Month],
       SUM(m.PlanningMH) AS PlanningMH, SUM(m.DisciplineMH) AS DisciplineMH, SUM(m.FinalMH) AS FinalMH, SUM(m.AfterMH) AS AfterMH,
       (CEILING(d.Permanent * 0.9) + d.Temporary) * 150.0 AS CapacityMH
FROM reporting.vwLineMonths m
JOIN dbo.Departments d ON d.Code = m.DeptCode
GROUP BY m.Cycle, m.DeptCode, d.Name, m.[Month], d.Permanent, d.Temporary;
GO

/* every department request with its decision and reason */
CREATE OR ALTER VIEW reporting.vwCommentItems AS
SELECT c.Name AS Cycle, p.Id AS ProposalId, p.DeptCode, COALESCE(l.ProjectCode, p.NewProjectCode) AS ProjectCode,
       COALESCE(l.Activity, p.NewActivity) AS Activity, COALESCE(l.Category, p.NewCategory) AS Category, p.Kind, p.Status,
       p.ChangeTypes, p.RequestedCategory, p.Reason, p.CreatedBy, p.SubmittedAt, p.ReviewedBy, p.ReviewedAt, p.ReviewComment,
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
