namespace NepPOS.Presentation.Register.Services;

/// <summary>
/// Drives a single shared on-screen keyboard overlay. Any component
/// requests focus on an input via RequestInput; the keyboard component
/// (mounted once, globally) reacts to StateChanged and shows itself.
/// </summary>
public sealed class OnScreenKeyboardService
{
    public bool IsVisible { get; private set; }
    public string CurrentValue { get; private set; } = string.Empty;
    public bool IsNumericOnly { get; private set; }

    private Action<string>? _onValueChanged;
    private Action? _onSubmit;

    public event Action? StateChanged;

    public void RequestInput(string initialValue, bool numericOnly, Action<string> onValueChanged, Action? onSubmit = null)
    {
        CurrentValue = initialValue;
        IsNumericOnly = numericOnly;
        _onValueChanged = onValueChanged;
        _onSubmit = onSubmit;
        IsVisible = true;
        StateChanged?.Invoke();
    }

    public void AppendKey(string key)
    {
        CurrentValue += key;
        _onValueChanged?.Invoke(CurrentValue);
        StateChanged?.Invoke();
    }

    public void Backspace()
    {
        if (CurrentValue.Length == 0) return;
        CurrentValue = CurrentValue[..^1];
        _onValueChanged?.Invoke(CurrentValue);
        StateChanged?.Invoke();
    }

    public void Submit()
    {
        _onSubmit?.Invoke();
        Close();
    }

    public void Close()
    {
        IsVisible = false;
        _onValueChanged = null;
        _onSubmit = null;
        StateChanged?.Invoke();
    }
}