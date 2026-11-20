namespace Times.Tray;

/// <summary>How long ago the user last used the keyboard or mouse, from the OS.</summary>
internal interface IIdleSource
{
    /// <summary>The time since the last input, or <c>null</c> when the OS cannot tell.</summary>
    public TimeSpan? SinceLastInput();
}
