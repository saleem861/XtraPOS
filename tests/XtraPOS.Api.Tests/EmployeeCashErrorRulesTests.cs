using XtraPOS.Api.Sales;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class EmployeeCashErrorRulesTests
{
    private static readonly Guid Ahmad = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Sami = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly DateTime Nov10 = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    private static CashErrorItem Item(Guid employee, decimal amount, string status = EmployeeCashErrorRules.StatusOpen, string kind = "Error", string? decision = null) =>
        new(Guid.NewGuid(), kind, employee, employee == Ahmad ? "أحمد" : "سامي", Guid.NewGuid(), "Labtop", 1, "كاونتر 1",
            amount, Nov10, Nov10, "سبب", decision, status, null, null, null, null, null, null, 0);

    private static Dictionary<Guid, CashErrorItem> Known(params CashErrorItem[] items) => items.ToDictionary(item => item.ItemId);

    [Fact]
    public void Status_prefers_waiver_then_closing_then_deferral()
    {
        Assert.Equal(EmployeeCashErrorRules.StatusWaived, EmployeeCashErrorRules.StatusOf(CashDrawerRules.Waive, "Settle", Nov10));
        Assert.Equal(EmployeeCashErrorRules.StatusOffset, EmployeeCashErrorRules.StatusOf(null, "Offset", Nov10));
        Assert.Equal(EmployeeCashErrorRules.StatusSettled, EmployeeCashErrorRules.StatusOf(CashDrawerRules.KeepShortage, "Settle", null));
        Assert.Equal(EmployeeCashErrorRules.StatusDeferred, EmployeeCashErrorRules.StatusOf(null, null, Nov10));
        Assert.Equal(EmployeeCashErrorRules.StatusOpen, EmployeeCashErrorRules.StatusOf(null, null, null));
    }

    [Fact]
    public void Summary_counts_signed_amounts_and_residuals_only_in_the_open_net()
    {
        var summary = EmployeeCashErrorRules.Summarize(
        [
            Item(Ahmad, -10m, EmployeeCashErrorRules.StatusOffset),
            Item(Ahmad, 4m, EmployeeCashErrorRules.StatusOffset),
            Item(Ahmad, -6m, kind: "Residual"),
            Item(Ahmad, -3m, EmployeeCashErrorRules.StatusWaived, decision: CashDrawerRules.Waive),
            Item(Ahmad, -2m, EmployeeCashErrorRules.StatusSettled),
            Item(Ahmad, -5m, EmployeeCashErrorRules.StatusDeferred),
        ]);

        Assert.Equal(6, summary.Count);
        Assert.Equal(2, summary.OpenCount);
        Assert.Equal(-20m, summary.Shortage);
        Assert.Equal(4m, summary.Surplus);
        Assert.Equal(-3m, summary.Waived);
        Assert.Equal(-2m, summary.Settled);
        Assert.Equal(-11m, summary.OpenNet);
    }

    [Fact]
    public void Offset_needs_two_open_errors_of_one_employee_and_keeps_the_net()
    {
        var shortage = Item(Ahmad, -10m);
        var surplus = Item(Ahmad, 4m);
        var other = Item(Sami, -1m);
        var settled = Item(Ahmad, -2m, EmployeeCashErrorRules.StatusSettled);
        var known = Known(shortage, surplus, other, settled);

        var one = Assert.Throws<CashErrorException>(() => EmployeeCashErrorRules.Validate(
            new CashErrorActionRequest("Offset", Ahmad, [shortage.ItemId], null, null, null), known));
        Assert.Contains("خطأين", one.Message);

        Assert.Throws<CashErrorException>(() => EmployeeCashErrorRules.Validate(
            new CashErrorActionRequest("Offset", Ahmad, [shortage.ItemId, other.ItemId], null, null, null), known));
        Assert.Throws<CashErrorException>(() => EmployeeCashErrorRules.Validate(
            new CashErrorActionRequest("Offset", Ahmad, [shortage.ItemId, settled.ItemId], null, null, null), known));

        var draft = EmployeeCashErrorRules.Validate(
            new CashErrorActionRequest("Offset", Ahmad, [shortage.ItemId, surplus.ItemId, shortage.ItemId], null, null, null), known);
        Assert.Equal(-6m, draft.Amount);
        Assert.Equal(2, draft.Items.Count);
    }

    [Fact]
    public void Settle_needs_a_method_and_defer_moves_to_the_first_of_a_later_month()
    {
        var shortage = Item(Ahmad, -10m);
        var known = Known(shortage);

        Assert.Throws<CashErrorException>(() => EmployeeCashErrorRules.Validate(
            new CashErrorActionRequest("Settle", Ahmad, [shortage.ItemId], null, null, null), known));
        var settle = EmployeeCashErrorRules.Validate(
            new CashErrorActionRequest("Settle", Ahmad, [shortage.ItemId], "payroll", null, "خصم الراتب"), known);
        Assert.Equal("Payroll", settle.Method);
        Assert.Equal(-10m, settle.Amount);

        var defer = EmployeeCashErrorRules.Validate(new CashErrorActionRequest("Defer", Ahmad, [shortage.ItemId], null, null, null), known);
        Assert.Equal(new DateTime(2026, 12, 1), defer.DeferTo);
        Assert.Equal(0m, defer.Amount);
        Assert.Throws<CashErrorException>(() => EmployeeCashErrorRules.Validate(
            new CashErrorActionRequest("Defer", Ahmad, [shortage.ItemId], null, new DateTime(2026, 11, 20), null), known));
    }

    [Fact]
    public void Notes_are_allowed_on_closed_errors_but_need_text()
    {
        var settled = Item(Ahmad, -2m, EmployeeCashErrorRules.StatusSettled);
        var known = Known(settled);
        Assert.Throws<CashErrorException>(() => EmployeeCashErrorRules.Validate(
            new CashErrorActionRequest("Note", Ahmad, [settled.ItemId], null, null, "  "), known));
        var note = EmployeeCashErrorRules.Validate(new CashErrorActionRequest("Note", Ahmad, [settled.ItemId], null, null, " دفع نقداً "), known);
        Assert.Equal("دفع نقداً", note.Note);
    }

    [Fact]
    public void Balances_list_the_most_owing_employee_first()
    {
        var balances = EmployeeCashErrorRules.Balances([Item(Sami, -1m), Item(Ahmad, -9m), Item(Ahmad, 2m)]);
        Assert.Equal(Ahmad, balances[0].EmployeeId);
        Assert.Equal(-7m, balances[0].Summary.OpenNet);
        Assert.Equal(Sami, balances[1].EmployeeId);
    }
}
