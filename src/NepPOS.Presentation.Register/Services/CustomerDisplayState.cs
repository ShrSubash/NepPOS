namespace NepPOS.Presentation.Register.Services;

/// <summary>One line on the customer display.</summary>
public sealed record DisplayLine(string Name, int Quantity, decimal Total);

/// <summary>
/// What the customer-facing screen shows. The register page puts the basket here as it changes, the tender panel
/// puts the payment result here, and the display window draws whatever is current. Shared by both windows.
/// </summary>
public sealed class CustomerDisplayState
{
    public enum DisplayMode { Idle, Selling, Paid }

    private readonly object _gate = new();

    public DisplayMode Mode { get; private set; } = DisplayMode.Idle;
    public IReadOnlyList<DisplayLine> Lines { get; private set; } = Array.Empty<DisplayLine>();
    public decimal Total { get; private set; }
    public decimal Savings { get; private set; }
    public decimal Paid { get; private set; }
    public decimal Change { get; private set; }

    public event Action? Changed;

    /// <summary>The basket as it is now. Ignored while the "thank you" screen is up, until the basket is cleared.</summary>
    public void ShowBasket(IReadOnlyList<DisplayLine> lines, decimal total, decimal savings)
    {
        ArgumentNullException.ThrowIfNull(lines);
        lock (_gate)
        {
            if (Mode == DisplayMode.Paid && lines.Count > 0)
                return;

            var mode = lines.Count == 0 ? DisplayMode.Idle : DisplayMode.Selling;
            if (mode == Mode && total == Total && savings == Savings && lines.SequenceEqual(Lines))
                return;   // nothing new: the register re-renders often

            (Mode, Lines, Total, Savings) = (mode, lines.ToList(), total, savings);
        }
        Changed?.Invoke();
    }

    /// <summary>The sale is paid: show the total, what was paid and the change.</summary>
    public void ShowPaid(decimal total, decimal paid, decimal change)
    {
        lock (_gate)
            (Mode, Total, Paid, Change) = (DisplayMode.Paid, total, paid, change);
        Changed?.Invoke();
    }

    /// <summary>Back to the welcome screen (new sale started with an empty basket).</summary>
    public void Clear()
    {
        lock (_gate)
            (Mode, Lines, Total, Savings, Paid, Change) = (DisplayMode.Idle, Array.Empty<DisplayLine>(), 0, 0, 0, 0);
        Changed?.Invoke();
    }
}
