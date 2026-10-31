namespace Times.Tests;

public sealed class MouseInputTests
{
    public static TheoryData<string, string> Sequences => new()
    {
        // AC-5: SGR wheel, with and without modifiers (4 Shift, 8 Alt, 16 Ctrl).
        { "[<64;10;5M", nameof(MouseInputKind.WheelUp) },
        { "[<65;10;5M", nameof(MouseInputKind.WheelDown) },
        { "[<80;1;1M", nameof(MouseInputKind.WheelUp) },
        { "[<81;1;1M", nameof(MouseInputKind.WheelDown) },
        { "[<68;1;1M", nameof(MouseInputKind.WheelUp) },
        { "[<72;1;1M", nameof(MouseInputKind.WheelUp) },
        { "[<93;1;1M", nameof(MouseInputKind.WheelDown) },
        { "[<64;300;200M", nameof(MouseInputKind.WheelUp) },

        // Horizontal wheel, clicks, releases and drags.
        { "[<66;10;5M", nameof(MouseInputKind.OtherMouse) },
        { "[<67;10;5M", nameof(MouseInputKind.OtherMouse) },
        { "[<0;10;5M", nameof(MouseInputKind.OtherMouse) },
        { "[<0;10;5m", nameof(MouseInputKind.OtherMouse) },
        { "[<2;10;5M", nameof(MouseInputKind.OtherMouse) },
        { "[<32;11;5M", nameof(MouseInputKind.OtherMouse) },
        { "[<96;11;5M", nameof(MouseInputKind.OtherMouse) },
        { "[<64;10;5m", nameof(MouseInputKind.OtherMouse) },

        // X10: button byte minus 32.
        { "[M`!!", nameof(MouseInputKind.WheelUp) },
        { "[Ma!!", nameof(MouseInputKind.WheelDown) },
        { "[Mp!!", nameof(MouseInputKind.WheelUp) },
        { "[M !!", nameof(MouseInputKind.OtherMouse) },
        { "[Mb!!", nameof(MouseInputKind.OtherMouse) },

        // Other sequences: discarded.
        { "[A", nameof(MouseInputKind.Discard) },
        { "[5~", nameof(MouseInputKind.Discard) },
        { "[1;5D", nameof(MouseInputKind.Discard) },
        { "[<64;10M", nameof(MouseInputKind.Discard) },
        { "[<64;10;5;1M", nameof(MouseInputKind.Discard) },
        { "[<x;10;5M", nameof(MouseInputKind.Discard) },
        { "[<-1;10;5M", nameof(MouseInputKind.Discard) },
        { "[<99999999999;1;1M", nameof(MouseInputKind.Discard) },
        { "[<64;10;5~", nameof(MouseInputKind.Discard) },
        { "[M`!", nameof(MouseInputKind.Discard) },
        { "[", nameof(MouseInputKind.Discard) },

        // Not a sequence at all.
        { "", nameof(MouseInputKind.NotASequence) },
        { "a", nameof(MouseInputKind.NotASequence) },
        { "O", nameof(MouseInputKind.NotASequence) },
    };

    // AC-5
    [Theory]
    [MemberData(nameof(Sequences))]
    public void Decode_ClassifiesTheSequence(string keys, string expected)
    {
        Assert.Equal(expected, MouseInput.Decode(keys).ToString());
    }

    // AC-5
    [Fact]
    public void Decode_AtTheKeyLimit_IsDiscarded()
    {
        string keys = "[<" + new string('1', MouseInput.MaxKeys - 2);

        Assert.True(MouseInput.IsComplete(keys));
        Assert.Equal(MouseInputKind.Discard, MouseInput.Decode(keys));
        Assert.False(MouseInput.IsComplete(keys[..^1]));
    }

    [Theory]
    [InlineData("[", false)]
    [InlineData("[<", false)]
    [InlineData("[<64;10;5", false)]
    [InlineData("[<64;10;5M", true)]
    [InlineData("[<0;10;5m", true)]
    [InlineData("[M", false)]
    [InlineData("[M`!", false)]
    [InlineData("[M`!!", true)]
    [InlineData("[A", true)]
    [InlineData("[5", false)]
    [InlineData("[5~", true)]
    [InlineData("[@", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    public void IsComplete_EndsAtTheFinalCharacter(string keys, bool expected)
    {
        Assert.Equal(expected, MouseInput.IsComplete(keys));
    }
}
