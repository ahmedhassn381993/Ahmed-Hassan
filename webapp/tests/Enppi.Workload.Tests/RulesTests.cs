using Enppi.Workload.Api.Domain;

namespace Enppi.Workload.Tests;

public class RulesTests
{
    private static double?[] P(params double?[] v) => v;

    [Fact]
    public void Blank_feedback_keeps_the_planner_value_and_zero_removes_it()
    {
        Assert.Equal(120, Rules.DisciplineValue(120, null));
        Assert.Equal(0, Rules.DisciplineValue(120, 0));
        Assert.Equal(80, Rules.DisciplineValue(120, 80));
    }

    [Fact]
    public void Final_value_is_approved_where_approved_else_planner()
    {
        Assert.Equal(90, Rules.FinalValue(new ForecastValue { PlannerMH = 100, ApprovedMH = 90 }));
        Assert.Equal(100, Rules.FinalValue(new ForecastValue { PlannerMH = 100 }));
    }

    [Fact]
    public void Increase_beyond_the_planned_finish_is_a_schedule_extension()
    {
        var c = Rules.Classify(new double[] { 100, 100, 0, 0 }, P(null, 150, 100, null));
        Assert.Contains("Increased MH", c.Types);
        Assert.Contains("Schedule Extension", c.Types);
        Assert.Equal(150, c.GrossIncrease);
        Assert.Equal(100, c.ExtensionMH);
        Assert.Equal(new[] { 1, 2 }, c.MonthsChanged);
    }

    [Fact]
    public void Offsetting_moves_are_a_monthly_redistribution()
    {
        var c = Rules.Classify(new double[] { 100, 100, 100 }, P(50, 150, null));
        Assert.Equal("Monthly Redistribution", c.Types[0]);
        Assert.Equal(0, c.Net);
    }

    [Fact]
    public void All_months_set_to_zero_is_a_removed_allocation()
    {
        var c = Rules.Classify(new double[] { 100, 100 }, P(0, 0));
        Assert.Equal("Removed Allocation", c.Types[0]);
        Assert.Equal(-200, c.Net);
    }

    [Fact]
    public void Earlier_finish_is_an_acceleration_and_earlier_work_an_earlier_start()
    {
        var acc = Rules.Classify(new double[] { 0, 100, 100, 100 }, P(null, null, null, 0));
        Assert.Contains("Schedule Acceleration", acc.Types);
        var early = Rules.Classify(new double[] { 0, 100, 100 }, P(50, null, null));
        Assert.Contains("Earlier Start", early.Types);
        Assert.Equal(50, early.EarlyStartMH);
    }

    [Fact]
    public void Equal_or_blank_values_are_no_change()
    {
        var c = Rules.Classify(new double[] { 100, 100 }, P(100, null));
        Assert.Empty(c.Types);
        Assert.Empty(c.MonthsChanged);
    }

    [Theory]
    [InlineData(60000, 6)]
    [InlineData(25000, 5)]
    [InlineData(12000, 4)]
    [InlineData(6000, 3)]
    [InlineData(2500, 2)]
    [InlineData(600, 1)]
    [InlineData(100, 0)]
    public void Severity_gross_change_tiers_include_the_50k_tier(double gross, int expected)
    {
        // planning large enough that the % term is 0; one department; no months, new or extension points
        var (pts, _) = Rules.Severity(gross, gross, 10_000_000, 1, 0, false, false);
        Assert.Equal(expected, pts);
    }

    [Fact]
    public void Severity_adds_percent_breadth_duration_new_and_extension()
    {
        var (pts, level) = Rules.Severity(100_000, 100_000, 0, 9, 13, true, true);
        Assert.Equal(6 + 2 + 3 + 2 + 1 + 1, pts);
        Assert.Equal("Critical", level);
        Assert.Equal("High", Rules.Severity(25000, 25000, 100000, 2, 6, false, false).Level);   // 5+1+1+1 = 8
    }

    [Fact]
    public void Capacity_uses_ninety_percent_of_permanent_rounded_up_plus_temporary()
    {
        var d = new Department { Permanent = 11, Temporary = 2 };
        var s = new WorkloadSettings();
        Assert.Equal(12, Rules.EffectiveHeads(d, s));     // ceil(9.9) + 2
        Assert.Equal(1800, Rules.CapacityPerMonth(d, s));
    }

    [Fact]
    public void Bulk_approval_and_justification_thresholds()
    {
        var s = new WorkloadSettings();
        Assert.True(Rules.IsBulkApprovable(400, 10_000, s));
        Assert.False(Rules.IsBulkApprovable(600, 100_000, s));    // above 500 MH
        Assert.False(Rules.IsBulkApprovable(400, 5_000, s));      // 8% of planning
        var big = Rules.Classify(new double[] { 1000, 1000 }, P(2500, 1600));
        Assert.True(Rules.NeedsJustification(big, 2000, s));
        var small = Rules.Classify(new double[] { 1000, 1000 }, P(1050, null));
        Assert.False(Rules.NeedsJustification(small, 2000, s));
    }

    [Theory]
    [InlineData("Running", Category.Running)]
    [InlineData("Internal / Unforeseen", Category.Internal)]
    [InlineData("Loan To", Category.LoanTo)]
    [InlineData("Under Signature", Category.UnderSignature)]
    [InlineData("Proposal", Category.Proposal)]
    [InlineData("Potential Opportunity", Category.Potential)]
    public void Category_labels_parse(string label, Category expected)
    {
        Assert.Equal(expected, Categories.Parse(label));
        Assert.Equal(expected, Categories.Parse(Categories.Label(expected)));
    }

    [Fact]
    public void Base_code_keeps_full_eight_digit_codes()
    {
        Assert.Equal("5248200", Rules.BaseCode("5248200-1"));
        Assert.Equal("40061420", Rules.BaseCode("40061420"));
    }
}
