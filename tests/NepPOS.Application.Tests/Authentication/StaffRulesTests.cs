using NepPOS.Application.Authentication;
using NepPOS.Domain.Enums;
using Xunit;

namespace NepPOS.Application.Tests.Authentication;

public class StaffRulesTests
{
    [Theory]
    [InlineData("4826")]
    [InlineData("90513")]
    [InlineData("731904")]
    public void Good_pins_are_accepted(string pin) => Assert.Null(StaffRules.PinProblem(pin));

    [Theory]
    [InlineData("")]
    [InlineData("482")]       // too short
    [InlineData("4826173")]   // longer than the sign-in pad
    [InlineData("48a6")]
    [InlineData("1111")]      // one digit repeated
    [InlineData("1234")]      // a run
    [InlineData("654321")]
    public void Weak_or_wrong_pins_are_refused(string pin) => Assert.NotNull(StaffRules.PinProblem(pin));

    [Fact]
    public void Owners_manage_everyone()
    {
        foreach (var role in Enum.GetValues<EmployeeRole>())
            Assert.True(StaffRules.CanManage(EmployeeRole.Owner, role));
    }

    [Fact]
    public void Managers_manage_cashiers_and_supervisors_only()
    {
        Assert.Equal(new[] { EmployeeRole.Cashier, EmployeeRole.ShiftSupervisor }, StaffRules.AssignableRoles(EmployeeRole.Manager));
        Assert.False(StaffRules.CanManage(EmployeeRole.Manager, EmployeeRole.Manager));
        Assert.False(StaffRules.CanManage(EmployeeRole.Manager, EmployeeRole.Owner));
    }

    [Fact]
    public void Cashiers_manage_nobody() => Assert.Empty(StaffRules.AssignableRoles(EmployeeRole.Cashier));

    [Fact]
    public void Names_are_required() => Assert.NotNull(StaffRules.NameProblem("   "));
}
