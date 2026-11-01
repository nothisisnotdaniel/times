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
        { "[M", nameof(MouseInputKind.Discard) }, // AC-3 (065): ESC [ M is just an unknown CSI.
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

    // AC-5 (066): the key limit no longer ends a sequence; only its final character does.
    [Fact]
    public void IsComplete_PastTheKeyLimit_WaitsForTheFinalCharacter()
    {
        string keys = "[" + new string('1', 40);

        Assert.False(MouseInput.IsComplete(keys));
        Assert.False(MouseInput.IsComplete("[<" + new string('1', MouseInput.MaxKeys - 2)));
        Assert.True(MouseInput.IsComplete(keys + "~"));
        Assert.Equal(MouseInputKind.Discard, MouseInput.Decode(keys + "~"));
    }

    // AC-3, AC-5 (066)
    [Fact]
    public void Decode_LongerThanTheKeyLimit_IsDiscarded()
    {
        string wheel = "[<64;" + new string('1', 40) + "M";
        string atLimit = "[<64;1;" + new string('0', MouseInput.MaxKeys - 9) + "1M";

        Assert.Equal(MouseInput.MaxKeys, atLimit.Length);
        Assert.Equal(MouseInputKind.WheelUp, MouseInput.Decode(atLimit));
        Assert.Equal(MouseInputKind.Discard, MouseInput.Decode(wheel));
        Assert.Equal(MouseInputKind.Discard, MouseInput.Decode("[<64;1;" + new string('0', MouseInput.MaxKeys - 8) + "1M"));
    }

    // AC-6, AC-9 (066)
    [Theory]
    [InlineData("[200~", nameof(MouseInputKind.PasteStart))]
    [InlineData("[201~", nameof(MouseInputKind.PasteEnd))]
    [InlineData("[202~", nameof(MouseInputKind.Discard))]
    [InlineData("[2000~", nameof(MouseInputKind.Discard))]
    [InlineData("[200;1~", nameof(MouseInputKind.Discard))]
    public void Decode_RecognisesThePasteMarkers(string keys, string expected)
    {
        Assert.True(MouseInput.IsComplete(keys));
        Assert.Equal(expected, MouseInput.Decode(keys).ToString());
    }

    [Theory]
    [InlineData("[", false)]
    [InlineData("[<", false)]
    [InlineData("[<64;10;5", false)]
    [InlineData("[<64;10;5M", true)]
    [InlineData("[<0;10;5m", true)]
    [InlineData("[M", true)] // AC-3 (065)
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
