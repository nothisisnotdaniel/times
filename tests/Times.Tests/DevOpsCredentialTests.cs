namespace Times.Tests;

public class DevOpsCredentialTests
{
    private const string Marker = "PAT-MARKER-9f3c";

    [Theory]
    [InlineData("a")]
    [InlineData("contoso")]
    [InlineData("Contoso-Dev-01")]
    [InlineData("0")]
    [InlineData("abcdefghijabcdefghijabcdefghijabcdefghijabcdefghij")] // 50
    public void Organization_Valid(string organization)
    {
        Assert.True(DevOpsCredential.IsValidOrganization(organization));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-contoso")]
    [InlineData("contoso-")]
    [InlineData("con toso")]
    [InlineData("contoso/evil")]
    [InlineData("contoso.example.com")]
    [InlineData("contoso\n")]
    [InlineData("con@toso")]
    [InlineData("cöntoso")]
    [InlineData("abcdefghijabcdefghijabcdefghijabcdefghijabcdefghijk")] // 51
    public void Organization_Invalid(string? organization)
    {
        Assert.False(DevOpsCredential.IsValidOrganization(organization));
    }

    [Fact]
    public void Pat_Valid()
    {
        Assert.True(DevOpsCredential.IsValidPat("a"));
        Assert.True(DevOpsCredential.IsValidPat("!~abcXYZ019"));
        Assert.True(DevOpsCredential.IsValidPat(new string('x', 200)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab cd")]
    [InlineData("ab\tcd")]
    [InlineData("abc\n")]
    [InlineData("abc\u0001")]
    [InlineData("abcé")]
    [InlineData("abc ")]
    public void Pat_Invalid(string? pat)
    {
        Assert.False(DevOpsCredential.IsValidPat(pat));
    }

    [Fact]
    public void Pat_TooLong_IsInvalid()
    {
        Assert.False(DevOpsCredential.IsValidPat(new string('x', 201)));
    }

    [Fact]
    public void TryCreate_NeedsBothParts()
    {
        Assert.Null(DevOpsCredential.TryCreate("bad org", Marker));
        Assert.Null(DevOpsCredential.TryCreate("contoso", "bad pat"));
        DevOpsCredential? credential = DevOpsCredential.TryCreate("contoso", Marker);
        Assert.NotNull(credential);
        Assert.Equal("contoso", credential.Organization);
        Assert.Equal(Marker, credential.Pat);
    }

    // AC-11
    [Fact]
    public void ToString_NeverHoldsThePat()
    {
        DevOpsCredential credential = DevOpsCredential.TryCreate("contoso", Marker)!;

        Assert.DoesNotContain(Marker, credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, $"{credential}", StringComparison.Ordinal);
        Assert.Contains("contoso", credential.ToString(), StringComparison.Ordinal);
    }
}
