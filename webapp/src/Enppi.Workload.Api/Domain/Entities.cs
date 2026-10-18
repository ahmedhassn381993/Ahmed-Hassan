using System.ComponentModel.DataAnnotations;

namespace Enppi.Workload.Api.Domain;

/// <summary>Workload categories in the confirmed hierarchy. Internal / Unforeseen is part of Running but shown separately.</summary>
public enum Category { Running, Internal, LoanTo, UnderSignature, Proposal, Potential }

/// <summary>Forecast cycle stages, in order. Each stage opens one kind of editing.</summary>
public enum CycleStatus { Draft, PlannerEntry, DisciplineReview, Approval, Implementation, Locked }

public enum ProposalKind { Change, NewAllocation, CategoryChange }

public enum ProposalStatus { Draft, Submitted, Approved, PartiallyApproved, Returned, Rejected }

public enum DeptReviewStatus { NotStarted, InProgress, Submitted, NoChangeConfirmed, Returned }

public enum LineOrigin { Planner, Import, DepartmentRequest }

public static class Roles
{
    public const string CorporatePlanning = "CorporatePlanning";
    public const string Planner = "Planner";
    public const string DisciplineManager = "DisciplineManager";
    public const string Viewer = "Viewer";
    public const string Admin = "Admin";
    public static readonly string[] All = { CorporatePlanning, Planner, DisciplineManager, Viewer, Admin };
}

public static class Categories
{
    public static readonly Category[] Order =
        { Category.Running, Category.Internal, Category.LoanTo, Category.UnderSignature, Category.Proposal, Category.Potential };

    /// <summary>Running + Internal + Loan To + Under Signature: the firm workload used for utilisation.</summary>
    public static bool IsFirm(Category c) => c is Category.Running or Category.Internal or Category.LoanTo or Category.UnderSignature;

    /// <summary>Corporate Planning controls these values; planners and departments see them read-only.</summary>
    public static bool IsCorporateControlled(Category c) => c is Category.LoanTo or Category.Internal;

    public static string Label(Category c) => c switch
    {
        Category.Running => "Running",
        Category.Internal => "Internal / Unforeseen",
        Category.LoanTo => "Loan To",
        Category.UnderSignature => "Under Signature",
        Category.Proposal => "Proposal",
        Category.Potential => "Potential Opportunity",
        _ => c.ToString(),
    };

    public static Category Parse(string text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant().Replace("_", " ");
        if (t.StartsWith("internal")) return Category.Internal;
        if (t.StartsWith("running") || t == "run") return Category.Running;
        if (t.StartsWith("loan")) return Category.LoanTo;
        if (t.StartsWith("under") || t.Contains("signature")) return Category.UnderSignature;
        if (t.StartsWith("propos")) return Category.Proposal;
        if (t.StartsWith("potential")) return Category.Potential;
        throw new FormatException($"Unknown category '{text}'");
    }
}

public class Department
{
    [Key, MaxLength(8)] public string Code { get; set; } = "";
    [MaxLength(120)] public string Name { get; set; } = "";
    /// <summary>Parent for roll-ups, e.g. 2410-2450 roll up to 2400 Piping; 5110-5160 belong to 5100.</summary>
    [MaxLength(8)] public string? ParentCode { get; set; }
    public int Permanent { get; set; }
    public int Temporary { get; set; }
    public int Managers { get; set; }
    public int LoanToUnpaid { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Project
{
    [Key, MaxLength(20)] public string Code { get; set; } = "";
    [MaxLength(12)] public string BaseCode { get; set; } = "";
    [MaxLength(250)] public string Name { get; set; } = "";
    [MaxLength(60)] public string? Client { get; set; }
    /// <summary>Category in the ENPPI project master (P project, I internal, O overhead...).</summary>
    [MaxLength(4)] public string? MasterCategory { get; set; }
    /// <summary>False for codes raised by departments and not yet in the master.</summary>
    public bool InMaster { get; set; } = true;
    public bool IsActive { get; set; } = true;
}

public class ForecastCycle
{
    public int Id { get; set; }
    [MaxLength(40)] public string Name { get; set; } = "";
    public DateOnly FirstMonth { get; set; }
    public int MonthCount { get; set; } = 24;
    public CycleStatus Status { get; set; } = CycleStatus.Draft;
    public DateOnly? PlannerDue { get; set; }
    public DateOnly? ReviewDue { get; set; }
    public DateOnly? ApprovalDue { get; set; }
    public bool HasAfterData { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(120)] public string CreatedBy { get; set; } = "";
    public DateTime? LockedAt { get; set; }
    [MaxLength(120)] public string? LockedBy { get; set; }

    public DateOnly Month(int index) => FirstMonth.AddMonths(index);
}

/// <summary>One department / sub-department / category / project row of the forecast (the Excel row of today).</summary>
public class ForecastLine
{
    public int Id { get; set; }
    public int CycleId { get; set; }
    [MaxLength(8)] public string DeptCode { get; set; } = "";
    [MaxLength(8)] public string SectionCode { get; set; } = "";
    public Category Category { get; set; }
    [MaxLength(20)] public string ProjectCode { get; set; } = "";
    [MaxLength(250)] public string Activity { get; set; } = "";
    [MaxLength(120)] public string? PlannerUpn { get; set; }
    public LineOrigin Origin { get; set; } = LineOrigin.Planner;
    public List<ForecastValue> Values { get; set; } = new();
}

/// <summary>The three stored values of a line in one month. Final = Approved where a change was approved, else Planner.</summary>
public class ForecastValue
{
    public int LineId { get; set; }
    public int MonthIndex { get; set; }
    public double? PlannerMH { get; set; }
    public double? ApprovedMH { get; set; }
    public double? AfterMH { get; set; }
}

/// <summary>A department's request: change a line's months, add a new allocation, or move a line to another category.</summary>
public class Proposal
{
    public int Id { get; set; }
    public int CycleId { get; set; }
    public int? LineId { get; set; }
    public ForecastLine? Line { get; set; }
    [MaxLength(8)] public string DeptCode { get; set; } = "";
    public ProposalKind Kind { get; set; }
    public ProposalStatus Status { get; set; } = ProposalStatus.Draft;
    [MaxLength(2000)] public string Reason { get; set; } = "";
    [MaxLength(250)] public string? ChangeTypes { get; set; }
    public Category? RequestedCategory { get; set; }
    [MaxLength(20)] public string? NewProjectCode { get; set; }
    [MaxLength(250)] public string? NewActivity { get; set; }
    public Category? NewCategory { get; set; }
    [MaxLength(8)] public string? NewSectionCode { get; set; }
    [MaxLength(120)] public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedAt { get; set; }
    [MaxLength(120)] public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    [MaxLength(2000)] public string? ReviewComment { get; set; }
    [MaxLength(120)] public string? ImplementedBy { get; set; }
    public DateTime? ImplementedAt { get; set; }
    public List<ProposalValue> Values { get; set; } = new();
}

/// <summary>Proposed value for one month; null = no change (the planner value is kept, never read as zero).</summary>
public class ProposalValue
{
    public int ProposalId { get; set; }
    public int MonthIndex { get; set; }
    public double? ProposedMH { get; set; }
    public double? ApprovedMH { get; set; }
}

public class DepartmentReview
{
    public int CycleId { get; set; }
    [MaxLength(8)] public string DeptCode { get; set; } = "";
    public DeptReviewStatus Status { get; set; } = DeptReviewStatus.NotStarted;
    public DateTime? SubmittedAt { get; set; }
    [MaxLength(120)] public string? SubmittedBy { get; set; }
    public DateTime? LastReminderAt { get; set; }
    public int ReminderCount { get; set; }
    [MaxLength(500)] public string? Note { get; set; }
}

public class UserAccount
{
    [Key, MaxLength(120)] public string Upn { get; set; } = "";
    [MaxLength(120)] public string DisplayName { get; set; } = "";
    [MaxLength(40)] public string Role { get; set; } = Roles.Viewer;
    /// <summary>Comma-separated department codes a discipline manager may review (Piping manager: 2410,2420,...).</summary>
    [MaxLength(200)] public string? DeptCodes { get; set; }
    [MaxLength(200)] public string? Email { get; set; }
    public bool IsActive { get; set; } = true;

    public IEnumerable<string> Departments() =>
        (DeptCodes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public class AuditEntry
{
    public long Id { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    [MaxLength(120)] public string User { get; set; } = "";
    [MaxLength(60)] public string Action { get; set; } = "";
    [MaxLength(60)] public string Entity { get; set; } = "";
    [MaxLength(60)] public string EntityKey { get; set; } = "";
    public int? CycleId { get; set; }
    [MaxLength(4000)] public string? Details { get; set; }
}

public class Notification
{
    public long Id { get; set; }
    public int CycleId { get; set; }
    [MaxLength(8)] public string DeptCode { get; set; } = "";
    [MaxLength(200)] public string Recipient { get; set; } = "";
    [MaxLength(200)] public string Subject { get; set; } = "";
    [MaxLength(4000)] public string Body { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
    public bool Sent { get; set; }
    [MaxLength(500)] public string? Error { get; set; }
}

public class AppSetting
{
    [Key, MaxLength(60)] public string Key { get; set; } = "";
    [MaxLength(200)] public string Value { get; set; } = "";
}
