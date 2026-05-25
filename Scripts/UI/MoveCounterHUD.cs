using Godot;

/// <summary>
/// Bottom-center HUD displaying the local player's remaining moves for the current turn.
///
/// On Show(n): counts up 0→n with a punch-scale on arrival.
/// On Step():  instantly decrements by 1 with a quick scale punch.
/// On Hide():  fades out (called by OnTurnEnd only — does NOT auto-hide at 0).
/// Font size scales linearly with the value (min 34 px at 1 move, max 72 px at 20+).
/// </summary>
public partial class MoveCounterHUD : Control
{
    private static MoveCounterHUD _instance;

    private Label              _label;
    private int                _currentValue;
    private Tween              _fadeTween;
    private Tween              _countTween;
    private Tween              _punchTween;
    private AudioStreamPlayer  _popPlayer;

    private const float MinFontSize      = 34f;
    private const float MaxFontSize      = 72f;
    private const int   MaxValueForScale = 20;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void _Ready()
    {
        _instance = this;

        // Centered horizontally, anchored to bottom
        AnchorLeft   = 0.5f;
        AnchorRight  = 0.5f;
        AnchorTop    = 1f;
        AnchorBottom = 1f;
        OffsetLeft   = -90f;
        OffsetRight  =  90f;
        OffsetTop    = -100f;
        OffsetBottom =  -16f;
        PivotOffset  = new Vector2(90f, 42f); // scale from center

        _label = new Label();
        _label.HorizontalAlignment = HorizontalAlignment.Center;
        _label.VerticalAlignment   = VerticalAlignment.Center;
        _label.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var font = GD.Load<FontFile>("res://Assets/Fonts/Jersey10-Regular.ttf");
        if (font != null) _label.AddThemeFontOverride("font", font);

        _label.AddThemeColorOverride("font_color",        new Color(1f, 0.84f, 0f));
        _label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.85f));
        _label.AddThemeConstantOverride("shadow_offset_x", 2);
        _label.AddThemeConstantOverride("shadow_offset_y", 2);
        _label.AddThemeFontSizeOverride("font_size", (int)MinFontSize);
        AddChild(_label);

        var popClip = GD.Load<AudioStream>("res://Assets/Sound FX/clack.wav");
        _popPlayer = new AudioStreamPlayer { VolumeDb = -6f };
        if (popClip != null) _popPlayer.Stream = popClip;
        AddChild(_popPlayer);

        Modulate = new Color(1f, 1f, 1f, 0f);
        Visible  = false;
    }

    // ── Static API ────────────────────────────────────────────────────────────

    /// <summary>Reveals the counter and animates up to <paramref name="moves"/>.</summary>
    public static void Show(int moves)  => _instance?.StartShow(moves);

    /// <summary>Decrements the counter by one step (called once per square the piece crosses).</summary>
    public static void Step()           => _instance?.DoStep();

    /// <summary>Fades the counter out. Call only from OnTurnEnd.</summary>
    public static void Hide()           => _instance?.DoHide();

    /// <summary>Instantly drains counter to 0 and fades. Use for base exits where all dice are consumed at once.</summary>
    public static void DrainAll()       => _instance?.DoDrainAll();

    // ── Internal ──────────────────────────────────────────────────────────────

    private void StartShow(int target)
    {
        _countTween?.Kill();
        _punchTween?.Kill();
        _currentValue = target;
        Scale = Vector2.One;

        Visible = true;
        _fadeTween?.Kill();
        _fadeTween = CreateTween();
        _fadeTween.TweenProperty(this, "modulate:a", 1f, 0.25f)
                  .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);

        // Count up 0 → target
        float duration = Mathf.Clamp(target * 0.035f, 0.25f, 0.60f);
        _countTween = CreateTween();
        _countTween.TweenMethod(
            Callable.From((float v) => RefreshDisplay(Mathf.RoundToInt(v))),
            0f, (float)target, duration
        ).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);

        // Punch scale when the count lands
        _punchTween = CreateTween();
        _punchTween.TweenInterval(duration * 0.85f);
        _punchTween.TweenProperty(this, "scale", Vector2.One * 1.22f, 0.08f)
                   .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _punchTween.TweenProperty(this, "scale", Vector2.One, 0.14f)
                   .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
    }

    private void DoStep()
    {
        if (_currentValue <= 0) return;
        _countTween?.Kill();
        _currentValue--;

        if (_popPlayer?.Stream != null) _popPlayer.Play();
        RefreshDisplay(_currentValue);

        // Quick punch: pop, settle back
        _punchTween?.Kill();
        _punchTween = CreateTween();
        _punchTween.TweenProperty(this, "scale", Vector2.One * 1.18f, 0.04f);
        _punchTween.TweenProperty(this, "scale", Vector2.One, 0.09f)
                   .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);

        // When the counter drains to 0 naturally, fade it out.
        // OnTurnEnd also calls Hide() after awaiting the animation as a fallback.
        if (_currentValue == 0)
        {
            _punchTween.TweenInterval(0.35f);
            _punchTween.TweenCallback(Callable.From(DoHide));
        }
    }

    private void DoDrainAll()
    {
        _countTween?.Kill();
        _punchTween?.Kill();
        _currentValue = 0;
        if (_popPlayer?.Stream != null) _popPlayer.Play();
        RefreshDisplay(0);
        _punchTween = CreateTween();
        _punchTween.TweenInterval(0.35f);
        _punchTween.TweenCallback(Callable.From(DoHide));
    }

    private void DoHide()
    {
        _countTween?.Kill();
        _punchTween?.Kill();
        _fadeTween?.Kill();
        _fadeTween = CreateTween();
        _fadeTween.TweenProperty(this, "modulate:a", 0f, 0.45f)
                  .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        _fadeTween.TweenCallback(Callable.From(() => { Visible = false; Scale = Vector2.One; }));
    }

    private void RefreshDisplay(int value)
    {
        _label.Text = value.ToString();
        float t    = Mathf.Clamp((float)value / MaxValueForScale, 0f, 1f);
        int   size = (int)Mathf.Lerp(MinFontSize, MaxFontSize, t);
        _label.AddThemeFontSizeOverride("font_size", size);
    }
}
