using CodexAccountMonitor.Core.Services;

namespace CodexAccountMonitor.Tests;

public sealed class IdentityUtilitiesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MaskEmail_BlankInputReturnsNull(string? email)
    {
        Assert.Null(IdentityUtilities.MaskEmail(email));
    }

    [Theory]
    [InlineData("a@example.com", "a•••@example.com")]
    [InlineData("ab@example.com", "a•••@example.com")]
    [InlineData("account@example.com", "a••••••@example.com")]
    [InlineData("averylonglocalpart@example.com", "a••••••••@example.com")]
    [InlineData("  User@Example.COM  ", "U•••@Example.COM")]
    public void MaskEmail_ExposesOnlyFirstLocalCharacterAndDomain(string email, string expected)
    {
        Assert.Equal(expected, IdentityUtilities.MaskEmail(email));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    public void MaskEmail_MalformedAddressReturnsFullyMaskedPlaceholder(string email)
    {
        Assert.Equal("••••", IdentityUtilities.MaskEmail(email));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FingerprintEmail_BlankInputReturnsNull(string? email)
    {
        Assert.Null(IdentityUtilities.FingerprintEmail(email));
    }

    [Fact]
    public void FingerprintEmail_IsTrimmedCaseInsensitiveSha256Hex()
    {
        const string expected = "C5AC93927855FBDDE05D3CE09B70CCDD9D4E24EDE68B98159632CD3B2CCA3F49";

        Assert.Equal(expected, IdentityUtilities.FingerprintEmail("user@example.com"));
        Assert.Equal(expected, IdentityUtilities.FingerprintEmail("  USER@EXAMPLE.COM  "));
    }

    [Fact]
    public void FingerprintEmail_DifferentIdentitiesDoNotCollideInNormalUse()
    {
        var first = IdentityUtilities.FingerprintEmail("first@example.com");
        var second = IdentityUtilities.FingerprintEmail("second@example.com");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(64, first.Length);
        Assert.Equal(64, second.Length);
        Assert.NotEqual(first, second);
    }
}
