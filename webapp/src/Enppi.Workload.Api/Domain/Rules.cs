namespace Enppi.Workload.Api.Domain;

/// <summary>Configurable factors (Admin > Settings). Defaults are the ones used by the current Power BI and the Q3 2026 analysis.</summary>
public record WorkloadSettings
{
    public double PermanentEffectiveness { get; init; } = 0.9;
    public double HoursPerManMonth { get; init; } = 150;
    /// <summary>Bulk approval: project-level change below this share of planning and below <see cref="BulkMaxMH"/>.</summary>
    public double BulkMaxPct { get; init; } = 0.05;
    public double BulkMaxMH { get; init; } = 500;
    /// <summary>A change above this share or MH needs a fuller justification (at least <see cref="JustifyMinChars"/> characters).</summary>
    public double JustifyPct { get; init; } = 0.20;
    public double JustifyMH { get; init; } = 2000;
    public int JustifyMinChars { get; init; } = 40;
    public int ReminderDaysBefore { get; init; } = 2;
}

public record ChangeSummary(
    IReadOnlyList<string> Types, double GrossIncrease, double GrossReduction, double ExtensionMH, double EarlyStartMH,
    IReadOnlyList<int> MonthsChanged)
{
    public double Net => GrossIncrease + GrossReduction;
    public double GrossChange => GrossIncrease - GrossReduction;
    public string TypesText => string.Join("; ", Types);
    public string BriefCategories => string.Join("; ", Types.Select(Rules.BriefCategory).Distinct());
}

/// <summary>The comparison rules of the comments analysis engine, applied as each value is saved.</summary>
public static class Rules
{
    public const double Tol = 0.5;

    public static double EffectiveHeads(Department d, WorkloadSettings s) =>
        Math.Ceiling(d.Permanent * s.PermanentEffectiveness) + d.Temporary;

    public static double CapacityPerMonth(Department d, WorkloadSettings s) => EffectiveHeads(d, s) * s.HoursPerManMonth;

    /// <summary>Discipline-adjusted value: the proposed value where populated; a blank keeps the planner value.</summary>
    public static double DisciplineValue(double? planner, double? proposed) => proposed ?? planner ?? 0;

    /// <summary>Final value: approved where a department change was approved, otherwise the planner value.</summary>
    public static double FinalValue(ForecastValue v) => v.ApprovedMH ?? v.PlannerMH ?? 0;

    /// <summary>Classify a requested change the way the engine does (increase, reduction, redistribution, extension,
    /// earlier start, acceleration, later start, removal, new allocation, phase transfer).</summary>
    public static ChangeSummary Classify(IReadOnlyList<double> planner, IReadOnlyList<double?> proposed,
                                         ProposalKind kind = ProposalKind.Change, bool newPhase = false)
    {
        int n = planner.Count;
        var final = new double[n];
        double inc = 0, red = 0, ext = 0, early = 0;
        var changed = new List<int>();
        int pFirst = -1, pLast = -1, fFirst = -1, fLast = -1;
        for (int m = 0; m < n; m++)
        {
            final[m] = DisciplineValue(planner[m], m < proposed.Count ? proposed[m] : null);
            if (Math.Abs(planner[m]) >= Tol) { if (pFirst < 0) pFirst = m; pLast = m; }
            if (Math.Abs(final[m]) >= Tol) { if (fFirst < 0) fFirst = m; fLast = m; }
        }
        for (int m = 0; m < n; m++)
        {
            double ch = Math.Round(final[m] - planner[m], 2);
            if (Math.Abs(ch) < Tol) continue;
            changed.Add(m);
            if (ch > 0)
            {
                inc += ch;
                if (pFirst >= 0 && m > pLast) ext += ch;
                else if (pFirst >= 0 && m < pFirst) early += ch;
            }
            else red += ch;
        }
        var types = new List<string>();
        double pTotal = planner.Sum(), fTotal = final.Sum();
        if (kind == ProposalKind.NewAllocation)
        {
            types.Add(newPhase ? "New Phase Allocation" : "New Project");
        }
        else if (kind == ProposalKind.CategoryChange)
        {
            types.Add("Phase Transfer");
        }
        else if (changed.Count > 0)
        {
            double net = inc + red;
            if (pTotal >= Tol && fTotal < Tol) types.Add("Removed Allocation");
            else if (inc >= Tol && -red >= Tol && Math.Abs(net) <= 0.1 * (inc - red)) types.Add("Monthly Redistribution");
            else
            {
                types.Add(net > 0 ? "Increased MH" : "Reduced MH");
                if (inc >= Tol && -red >= Tol) types.Add("Monthly Redistribution");
            }
            if (ext >= Tol) types.Add("Schedule Extension");
            if (early >= Tol) types.Add("Earlier Start");
            if (pLast >= 0 && fLast >= 0 && fLast < pLast) types.Add("Schedule Acceleration");
            if (pFirst >= 0 && fFirst > pFirst) types.Add("Rescheduled (later start)");
        }
        return new ChangeSummary(types, inc, red, ext, early, changed);
    }

    /// <summary>The brief's eleven concern categories; engine sub-types map onto them.</summary>
    public static string BriefCategory(string type) => type switch
    {
        "Earlier Start" => "Schedule Acceleration",
        "Rescheduled (later start)" => "Monthly Redistribution",
        "Phase Label Change Only" => "Phase Transfer",
        "New Project (code reused - verify)" => "New Project",
        _ => type,
    };

    /// <summary>Severity points (same tiers as the analysis workbook): gross change, % deviation, departments raising,
    /// months affected, new allocation, schedule extension. Critical >= 10, High 7-9, Medium 4-6, Low below.</summary>
    public static (int Points, string Level) Severity(double grossChange, double net, double planningTotal, int deptsRaising,
                                                      int monthsAffected, bool hasNew, bool hasExtension)
    {
        int pts = grossChange >= 50000 ? 6 : grossChange >= 20000 ? 5 : grossChange >= 10000 ? 4 : grossChange >= 5000 ? 3
                : grossChange >= 2000 ? 2 : grossChange >= 500 ? 1 : 0;
        if (planningTotal <= 0) pts += 2;
        else
        {
            double pct = Math.Abs(net) / planningTotal;
            pts += pct >= 0.5 ? 2 : pct >= 0.2 ? 1 : 0;
        }
        pts += deptsRaising >= 6 ? 3 : deptsRaising >= 4 ? 2 : deptsRaising >= 2 ? 1 : 0;
        pts += monthsAffected >= 12 ? 2 : monthsAffected >= 6 ? 1 : 0;
        pts += (hasNew ? 1 : 0) + (hasExtension ? 1 : 0);
        string level = pts >= 10 ? "Critical" : pts >= 7 ? "High" : pts >= 4 ? "Medium" : "Low";
        return (pts, level);
    }

    /// <summary>Changes this large need a fuller justification before they can be submitted.</summary>
    public static bool NeedsJustification(ChangeSummary c, double planningTotal, WorkloadSettings s) =>
        c.GrossChange >= s.JustifyMH || (planningTotal > 0 && Math.Abs(c.Net) / planningTotal >= s.JustifyPct) ||
        (planningTotal <= 0 && c.GrossChange >= Tol);

    /// <summary>Small enough for bulk approval at project level.</summary>
    public static bool IsBulkApprovable(double projectGrossChange, double projectPlanning, WorkloadSettings s) =>
        projectGrossChange < s.BulkMaxMH && projectPlanning > 0 && projectGrossChange / projectPlanning < s.BulkMaxPct;

    /// <summary>Base code of a full project code: 5248200-1 -> 5248200, 40061420 -> 40061420.</summary>
    public static string BaseCode(string code) => (code ?? "").Split('-')[0].Trim();
}
