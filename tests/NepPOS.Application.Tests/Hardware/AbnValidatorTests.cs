using NepPOS.Application.Hardware;
using Xunit;

namespace NepPOS.Application.Tests.Hardware;

public class AbnValidatorTests
{
    [Theory]
    [InlineData("51824753556")]     // the ATO's own ABN (its standard worked example)
    [InlineData("51 824 753 556")]  // spaces as printed on invoices are fine
    public void IsValid_AcceptsAValidAbn(string abn) => Assert.True(AbnValidator.IsValid(abn));

    [Theory]
    [InlineData("51824753557")]     // one digit wrong -> checksum fails
    [InlineData("12345678901")]
    [InlineData("5182475355")]      // 10 digits
    [InlineData("51-824-753-556")]  // only spaces allowed as separators
    [InlineData("")]
    [InlineData(null)]
    public void IsValid_RejectsAnInvalidAbn(string? abn) => Assert.False(AbnValidator.IsValid(abn));
}
