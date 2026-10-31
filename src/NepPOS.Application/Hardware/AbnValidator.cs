namespace NepPOS.Application.Hardware;

/// <summary>
/// Checks an Australian Business Number with the ATO's published algorithm:
/// subtract 1 from the first digit, multiply each digit by its weight,
/// and the total must divide exactly by 89.
/// </summary>
public static class AbnValidator
{
    private static readonly int[] Weights = { 10, 1, 3, 5, 7, 9, 11, 13, 15, 17, 19 };

    public static bool IsValid(string? abn)
    {
        var digits = new string((abn ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length != 11 || (abn ?? "").Any(c => !char.IsDigit(c) && c != ' '))
            return false;

        var sum = 0;
        for (var i = 0; i < 11; i++)
        {
            var digit = digits[i] - '0';
            if (i == 0) digit -= 1;
            sum += digit * Weights[i];
        }

        return sum % 89 == 0;
    }
}
