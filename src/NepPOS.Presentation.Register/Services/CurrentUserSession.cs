using NepPOS.Domain.Entities;

namespace NepPOS.Presentation.Register.Services;

public sealed class CurrentUserSession
{
    public Employee? Employee { get; private set; }
    public bool IsAuthenticated => Employee is not null;

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