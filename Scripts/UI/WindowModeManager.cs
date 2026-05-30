using Godot;

/// <summary>
/// Promotes the OS "maximize" button to real fullscreen, and gives the player a
/// way back out (F11 / Alt+Enter / Escape) since fullscreen hides the window
/// decorations. Registered as an autoload so it survives scene changes.
/// </summary>
public partial class WindowModeManager : Node
{
    public override void _Ready()
    {
        // SizeChanged fires when the user hits the maximize button; we check the
        // resulting mode rather than polling every frame.
        GetWindow().SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged()
    {
        // The maximize button lands the window in Maximized mode — flip it to
        // borderless fullscreen instead. Use ExclusiveFullscreen if you want a
        // true exclusive video mode.
        if (DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Maximized)
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        // F11 / Alt+Enter toggle fullscreen (and are the way back out, since
        // fullscreen hides the maximize button). Escape is left alone so it
        // keeps driving the existing menu/quit handling.
        bool toggle = key.Keycode == Key.F11 ||
                      (key.Keycode == Key.Enter && key.AltPressed);
        if (toggle)
            DisplayServer.WindowSetMode(IsFullscreen()
                ? DisplayServer.WindowMode.Windowed
                : DisplayServer.WindowMode.Fullscreen);
    }

    private static bool IsFullscreen()
    {
        var mode = DisplayServer.WindowGetMode();
        return mode == DisplayServer.WindowMode.Fullscreen ||
               mode == DisplayServer.WindowMode.ExclusiveFullscreen;
    }
}
