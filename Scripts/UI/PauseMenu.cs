using Godot;

/// <summary>
/// Global pause / menu overlay. A "Tab" keycap hint sits in the top-right corner
/// at all times; pressing Tab blurs the game behind a dim panel that offers a
/// fullscreen toggle and a return-to-welcome-screen button. Tab (or the on-screen
/// hint) closes it again.
///
/// Registered as an autoload so it floats above every scene. It does NOT pause the
/// SceneTree — this is a networked game and the server keeps running, so freezing
/// the local tree would only desync us. The overlay just blurs and dims.
/// </summary>
public partial class PauseMenu : CanvasLayer
{
    private static readonly Font UiFont =
        GD.Load<FontFile>("res://Assets/Fonts/Jersey10-Regular.ttf");

    private Control      _overlay;
    private HBoxContainer _tabHint;
    private Button       _fullscreenButton;
    private bool         _open;
    private bool         _active;   // only true once the player is in the game world

    public override void _Ready()
    {
        // Above the in-game HUD CanvasLayers (which sit at layer 10).
        Layer = 100;
        ProcessMode = ProcessModeEnum.Always;

        BuildTabHint();
        BuildOverlay();
    }

    public override void _Input(InputEvent @event)
    {
        if (!_active) return;   // dormant on the welcome screen

        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Tab })
        {
            // Don't hijack Tab while the player is typing (e.g. chat / username).
            var focus = GetViewport().GuiGetFocusOwner();
            if (!_open && (focus is LineEdit || focus is TextEdit))
                return;

            Toggle();
            // Swallow Tab so it doesn't also drive Godot's focus-traversal.
            GetViewport().SetInputAsHandled();
        }
    }

    // ── Activation (driven by WelcomeScreen as it hands off to gameplay) ─────

    /// <summary>Enable the menu and fade the Tab hint in. Called once we enter the game.</summary>
    public void Activate()
    {
        if (_active) return;
        _active = true;
        var tween = CreateTween();
        tween.TweenProperty(_tabHint, "modulate:a", 1f, 0.6f)
             .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
    }

    /// <summary>Disable the menu and hide the hint instantly (e.g. back on the welcome screen).</summary>
    public void Deactivate()
    {
        _active = false;
        if (_open) Close();
        _tabHint.Modulate = new Color(1f, 1f, 1f, 0f);
    }

    // ── Toggle ──────────────────────────────────────────────────────────────

    private void Toggle()
    {
        if (_open) Close();
        else       Open();
    }

    private void Open()
    {
        _open = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        RefreshFullscreenLabel();
        _overlay.Visible = true;
    }

    private void Close()
    {
        _open = false;
        _overlay.Visible = false;
        // Hand the cursor back to whatever gameplay state owns it: it stays free
        // only while focused on a surface (board / clipboard); otherwise recapture
        // it so mouse-look drives the camera again.
        bool keepCursorFree = FocusController.Instance != null &&
                              FocusController.Instance.IsFocused;
        Input.MouseMode = keepCursorFree
            ? Input.MouseModeEnum.Visible
            : Input.MouseModeEnum.Captured;
    }

    // ── Button actions ──────────────────────────────────────────────────────

    private void OnFullscreenPressed()
    {
        var win = GetWindow();
        win.Mode = IsFullscreen(win) ? Window.ModeEnum.Windowed : Window.ModeEnum.Fullscreen;
        RefreshFullscreenLabel();
    }

    private void OnReturnPressed()
    {
        Close();
        // Tell the server we're leaving the room/match, then load the welcome scene
        // fresh. ChangeSceneToFile (not ReloadCurrentScene) because by now the
        // reparented MainScene is the "current scene" — reloading it would just
        // restart the game. AuthManager / LiveConnectionManager are static, so the
        // login and live connection survive the swap and the window stays open.
        LiveConnectionManager.SendLeaveRoom();
        GetTree().ChangeSceneToFile("res://Scenes/UI/WelcomeScreen.tscn");
    }

    private void RefreshFullscreenLabel()
    {
        if (_fullscreenButton == null) return;
        _fullscreenButton.Text = IsFullscreen(GetWindow()) ? "Windowed" : "Fullscreen";
    }

    private static bool IsFullscreen(Window win) =>
        win.Mode == Window.ModeEnum.Fullscreen ||
        win.Mode == Window.ModeEnum.ExclusiveFullscreen;

    // ── UI construction ─────────────────────────────────────────────────────

    private void BuildTabHint()
    {
        // Pinned to the top-right corner, always visible, never eats mouse input.
        var hbox = new HBoxContainer
        {
            MouseFilter  = Control.MouseFilterEnum.Ignore,
            AnchorLeft   = 1f, AnchorRight = 1f, AnchorTop = 0f, AnchorBottom = 0f,
            OffsetLeft   = -190f, OffsetRight = -16f,
            OffsetTop    = 16f,   OffsetBottom = 54f,
            GrowHorizontal = Control.GrowDirection.Begin,
            Alignment    = BoxContainer.AlignmentMode.End,
        };
        hbox.AddThemeConstantOverride("separation", 8);
        hbox.Modulate = new Color(1f, 1f, 1f, 0f);   // hidden until we enter the game
        AddChild(hbox);

        hbox.AddChild(MakeKeyCap("Tab"));
        hbox.AddChild(MakeText("Menu", 26));
        _tabHint = hbox;
    }

    private void BuildOverlay()
    {
        _overlay = new Control
        {
            Visible     = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            AnchorRight = 1f, AnchorBottom = 1f,
        };
        AddChild(_overlay);

        // Blurred screen capture (reuses the welcome-screen blur shader).
        var blur = new ColorRect { AnchorRight = 1f, AnchorBottom = 1f };
        var shader = GD.Load<Shader>("res://Shaders/blur.gdshader");
        if (shader != null)
        {
            var mat = new ShaderMaterial { Shader = shader };
            mat.SetShaderParameter("lod", 2.5f);
            blur.Material = mat;
        }
        blur.MouseFilter = Control.MouseFilterEnum.Ignore;
        _overlay.AddChild(blur);

        // Dim tint over the blur for contrast.
        var dim = new ColorRect
        {
            Color       = new Color(0f, 0f, 0f, 0.45f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorRight = 1f, AnchorBottom = 1f,
        };
        _overlay.AddChild(dim);

        // Centered menu column.
        var center = new CenterContainer { AnchorRight = 1f, AnchorBottom = 1f };
        center.MouseFilter = Control.MouseFilterEnum.Ignore;
        _overlay.AddChild(center);

        var vbox = new VBoxContainer { CustomMinimumSize = new Vector2(360, 0) };
        vbox.AddThemeConstantOverride("separation", 18);
        center.AddChild(vbox);

        var title = MakeText("Paused", 64);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        vbox.AddChild(title);

        _fullscreenButton = MakeButton("Fullscreen");
        _fullscreenButton.Pressed += OnFullscreenPressed;
        vbox.AddChild(_fullscreenButton);

        var returnButton = MakeButton("Return to Welcome Screen");
        returnButton.Pressed += OnReturnPressed;
        vbox.AddChild(returnButton);

        // "Tab to exit" footer.
        var footer = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        footer.AddThemeConstantOverride("separation", 8);
        footer.AddThemeConstantOverride("margin_top", 12);
        footer.AddChild(MakeKeyCap("Tab"));
        footer.AddChild(MakeText("to exit", 22));
        vbox.AddChild(footer);
    }

    // ── Styling helpers (match the in-game "Shift" keycap look) ─────────────

    private static Label MakeKeyCap(string text)
    {
        var label = new Label
        {
            Text                = text,
            CustomMinimumSize   = new Vector2(48, 30),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            SizeFlagsVertical   = Control.SizeFlags.ShrinkCenter,
            MouseFilter         = Control.MouseFilterEnum.Ignore,
        };

        var style = new StyleBoxFlat
        {
            BgColor     = new Color(0.08f, 0.08f, 0.08f, 0.85f),
            BorderColor = new Color(0.6f, 0.6f, 0.6f, 0.5f),
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(4);
        style.SetContentMarginAll(3f);
        label.AddThemeStyleboxOverride("normal", style);

        if (UiFont != null)
        {
            label.AddThemeFontOverride("font", UiFont);
            label.AddThemeFontSizeOverride("font_size", 16);
        }
        label.AddThemeColorOverride("font_color",         new Color(1f, 1f, 1f, 0.95f));
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 1f));
        label.AddThemeConstantOverride("outline_size", 3);
        return label;
    }

    private static Label MakeText(string text, int size)
    {
        var label = new Label
        {
            Text              = text,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter       = Control.MouseFilterEnum.Ignore,
        };
        if (UiFont != null)
        {
            label.AddThemeFontOverride("font", UiFont);
            label.AddThemeFontSizeOverride("font_size", size);
        }
        label.AddThemeColorOverride("font_color",         new Color(1f, 1f, 1f, 0.95f));
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 1f));
        label.AddThemeConstantOverride("outline_size", 4);
        return label;
    }

    private static Button MakeButton(string text)
    {
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(0, 48),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };

        var normal = new StyleBoxFlat
        {
            BgColor     = new Color(0.08f, 0.08f, 0.08f, 0.85f),
            BorderColor = new Color(0.6f, 0.6f, 0.6f, 0.5f),
        };
        normal.SetBorderWidthAll(2);
        normal.SetCornerRadiusAll(4);
        normal.SetContentMarginAll(10f);

        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor     = new Color(0.18f, 0.12f, 0.05f, 0.92f);
        hover.BorderColor = new Color(0.9f, 0.55f, 0.08f, 0.8f);

        var pressed = (StyleBoxFlat)normal.Duplicate();
        pressed.BgColor = new Color(0.05f, 0.05f, 0.05f, 0.95f);

        button.AddThemeStyleboxOverride("normal",  normal);
        button.AddThemeStyleboxOverride("hover",   hover);
        button.AddThemeStyleboxOverride("pressed", pressed);
        button.AddThemeStyleboxOverride("focus",   new StyleBoxEmpty());

        if (UiFont != null)
        {
            button.AddThemeFontOverride("font", UiFont);
            button.AddThemeFontSizeOverride("font_size", 28);
        }
        button.AddThemeColorOverride("font_color",         new Color(1f, 1f, 1f, 0.95f));
        button.AddThemeColorOverride("font_hover_color",   new Color(1f, 1f, 1f));
        button.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 1f));
        button.AddThemeConstantOverride("outline_size", 3);
        return button;
    }
}
