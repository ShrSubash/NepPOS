namespace NepPOS.Application.Abstractions;

/// <summary>
/// Who is signed in at this register right now. Implemented by the UI's session;
/// lets the Application layer stamp journal entries without depending on the UI.
/// </summary>
public interface ICurrentOperator
{
    int? EmployeeId { get; }
    string? EmployeeName { get; }
}
