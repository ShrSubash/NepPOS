using NepPOS.Application.Abstractions;
using NepPOS.Domain.Entities;

namespace NepPOS.Presentation.Register.Services;

public sealed class CurrentUserSession : ICurrentOperator
{
    public Employee? Employee { get; private set; }
    public bool IsAuthenticated => Employee is not null;

    // ICurrentOperator: lets the journal stamp entries with who is signed in.
    public int? EmployeeId => Employee?.Id;
    public string? EmployeeName => Employee?.FullName;

    public event Action? StateChanged;

    public void SignIn(Employee employee)
    {
        Employee = employee ?? throw new ArgumentNullException(nameof(employee));
        StateChanged?.Invoke();
    }

    public void SignOut()
    {
        Employee = null;
        StateChanged?.Invoke();
    }
}
