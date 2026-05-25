using Godot;

/// <summary>
/// Pixelated dice HUD anchored to the bottom-right corner.
///
/// Each die tile is a 90×90 SubViewportContainer with Stretch=true and
/// StretchShrink=3.  Godot auto-sets the inner SubViewport to 30×30
/// (container_size / shrink), then scales the texture back up to 90×90
/// with nearest-neighbour filtering — giving uniform 3×3-pixel blocks.
/// The panel uses a flat StyleBox (no corner radius) so its edges are also
/// pixel-crisp with no anti-aliased curves.
/// </summary>
public partial class DiceHUD : Control
{
    private static DiceHUD _instance;

    private readonly Node3D[] _displayDice = new Node3D[2];

    private static RigidBody3D[] _pendingDice;
    private static volatile bool _pendingAttach;
    private static volatile bool _pendingHide;

    // For showing remote players' dice results as static face orientations.
    private static volatile bool _pendingStaticShow;
    private static int  _pendingStatic1;
    private static int  _pendingStatic2;

    // Maps face value 1-6 to the die rotation that puts that face pointing up (+Y).
    // Face-axis mapping: +X=4, -X=3, +Y=2, -Y=5, +Z=6, -Z=1.
    // Rx(+π/2): local −Z → world +Y  → face 1 on top
    // Rx(−π/2): local +Z → world +Y  → face 6 on top
    private static readonly Vector3[] FaceRotations = new[]
    {
        Vector3.Zero,                                    // 0 (unused)
        new Vector3( Mathf.Pi / 2f, 0f, 0f),           // 1  (-Z → +Y)
        new Vector3(0f, 0f, 0f),                        // 2  (+Y → +Y)
        new Vector3(0f, 0f, -Mathf.Pi / 2f),           // 3  (-X → +Y)
        new Vector3(0f, 0f,  Mathf.Pi / 2f),           // 4  (+X → +Y)
        new Vector3(Mathf.Pi, 0f, 0f),                  // 5  (-Y → +Y)
        new Vector3(-Mathf.Pi / 2f, 0f, 0f),           // 6  (+Z → +Y)
    };

    private RigidBody3D[] _activeDice;
    private Tween         _fadeTween;

    private TextureRect   _rerollIcon;
    private Tween         _rerollTween;
    private bool          _rerollIconVisible;

    private static volatile bool _pendingShowRerollIcon;
    private static volatile bool _pendingHideRerollIcon;
    private static int           _pendingRerollDoubles;

    private TextureRect   _noMovesIcon;
    private Tween         _noMovesTween;

    private static volatile bool _pendingShowNoMovesIcon;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void _Ready()
    {
        _instance = this;

        // Bottom-right corner — same footprint as the original working version.
        AnchorLeft   = 1f;
        AnchorRight  = 1f;
        AnchorTop    = 1f;
        AnchorBottom = 1f;
        OffsetRight  = -40f;
        OffsetLeft   = -246f;  // 206 px wide
        OffsetTop    = -142f;  // 130 px tall
        OffsetBottom = -12f;

        var hbox = new HBoxContainer();
        hbox.SetAnchorsPreset(LayoutPreset.FullRect);
        hbox.AddThemeConstantOverride("separation", 8);
        AddChild(hbox);

        var diceScene = GD.Load<PackedScene>("res://Assets/PlayingSet/General/dice.glb");
        for (int i = 0; i < 2; i++)
            BuildDieViewport(i, hbox, diceScene);

        // Reroll icon — 64×64, centered above the dice tiles.
        // Floats up into position when shown; does not cover the dice.
        _rerollIcon = new TextureRect();
        _rerollIcon.AnchorLeft   = 0.5f;
        _rerollIcon.AnchorRight  = 0.5f;
        _rerollIcon.AnchorTop    = 0f;
        _rerollIcon.AnchorBottom = 0f;
        _rerollIcon.OffsetLeft   = -32f;
        _rerollIcon.OffsetRight  =  32f;
        _rerollIcon.OffsetTop    =   5f;  // start: just inside panel top
        _rerollIcon.OffsetBottom =  69f;
        _rerollIcon.PivotOffset  = new Vector2(32f, 32f); // rotate around own center
        _rerollIcon.ExpandMode   = TextureRect.ExpandModeEnum.FitWidth;
        _rerollIcon.StretchMode  = TextureRect.StretchModeEnum.KeepAspectCentered;
        _rerollIcon.Modulate     = new Color(1f, 1f, 1f, 0f);
        var rerollTex = GD.Load<Texture2D>("res://Assets/Icons/reroll_icon.svg");
        if (rerollTex != null) _rerollIcon.Texture = rerollTex;
        AddChild(_rerollIcon);

        _noMovesIcon = new TextureRect();
        _noMovesIcon.AnchorLeft   = 0.5f;
        _noMovesIcon.AnchorRight  = 0.5f;
        _noMovesIcon.AnchorTop    = 0f;
        _noMovesIcon.AnchorBottom = 0f;
        _noMovesIcon.OffsetLeft   = -32f;
        _noMovesIcon.OffsetRight  =  32f;
        _noMovesIcon.OffsetTop    =   5f;
        _noMovesIcon.OffsetBottom =  69f;
        _noMovesIcon.PivotOffset  = new Vector2(32f, 32f);
        _noMovesIcon.ExpandMode   = TextureRect.ExpandModeEnum.FitWidth;
        _noMovesIcon.StretchMode  = TextureRect.StretchModeEnum.KeepAspectCentered;
        _noMovesIcon.Modulate     = new Color(1f, 1f, 1f, 0f);
        var noMovesTex = GD.Load<Texture2D>("res://Assets/Icons/exclamation.svg");
        if (noMovesTex != null) _noMovesIcon.Texture = noMovesTex;
        AddChild(_noMovesIcon);

        Modulate = new Color(1f, 1f, 1f, 0f);
        Visible  = false;
    }

    private void BuildDieViewport(int index, HBoxContainer row, PackedScene diceScene)
    {
        // Display at 90×90; StretchShrink=3 makes Godot render the SubViewport
        // at 30×30 and scale up 3× with nearest-neighbour → chunky pixel blocks.
        // Do NOT set SubViewport.Size manually — Godot auto-calculates it from
        // container.size / StretchShrink when Stretch is true.
        var container = new SubViewportContainer();
        container.CustomMinimumSize   = new Vector2(110f, 110f);
        container.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        container.Stretch             = true;
        container.StretchShrink       = 3;
        container.TextureFilter       = TextureFilterEnum.Nearest;
        row.AddChild(container);

        var viewport = new SubViewport();
        viewport.OwnWorld3D             = true;   // isolated — no bleed into main scene
        viewport.TransparentBg          = true;
        viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        container.AddChild(viewport);

        var worldEnv = new WorldEnvironment();
        var env = new Godot.Environment();
        env.BackgroundMode     = Godot.Environment.BGMode.Color;
        env.BackgroundColor    = new Color(0f, 0f, 0f, 0f);
        env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        env.AmbientLightColor  = new Color(0.9f, 0.85f, 0.75f);
        env.AmbientLightEnergy = 0.45f;
        worldEnv.Environment   = env;
        viewport.AddChild(worldEnv);

        var cam = new Camera3D();
        cam.Position        = new Vector3(0f, 0.15f, 0f);
        cam.RotationDegrees = new Vector3(-90f, 0f, 0f);
        cam.Projection      = Camera3D.ProjectionType.Orthogonal;
        cam.Size            = 0.085f;
        viewport.AddChild(cam);

        var keyLight = new DirectionalLight3D();
        keyLight.RotationDegrees = new Vector3(-55f, -25f, 0f);
        keyLight.LightEnergy     = 1.7f;
        keyLight.LightColor      = new Color(1f, 0.95f, 0.85f);
        viewport.AddChild(keyLight);

        var fillLight = new DirectionalLight3D();
        fillLight.RotationDegrees = new Vector3(-25f, 155f, 0f);
        fillLight.LightEnergy     = 0.3f;
        fillLight.LightColor      = new Color(0.7f, 0.82f, 1.0f);
        viewport.AddChild(fillLight);

        if (diceScene != null)
        {
            var die = diceScene.Instantiate<Node3D>();
            viewport.AddChild(die);
            _displayDice[index] = die;
        }
    }

    // ── Per-frame ─────────────────────────────────────────────────────────────

    public override void _Process(double delta)
    {
        if (_pendingAttach)
        {
            _pendingAttach = false;
            _activeDice    = _pendingDice;
            ShowHUD();
        }

        if (_pendingStaticShow)
        {
            _pendingStaticShow = false;
            _activeDice        = null; // stop physics tracking
            SetFaceValue(0, _pendingStatic1);
            SetFaceValue(1, _pendingStatic2);
            ShowHUD();
        }

        if (_activeDice != null)
        {
            for (int i = 0; i < 2; i++)
            {
                if (i < _activeDice.Length && _displayDice[i] != null && _activeDice[i] != null)
                {
                    var euler = _activeDice[i].GlobalTransform.Basis.GetEuler();
                    _displayDice[i].Rotation = new Vector3(euler.X, 0f, euler.Z);
                }
            }
        }

        if (_pendingHide)
        {
            _pendingHide = false;
            _activeDice  = null;
            HideHUD();
        }

        if (_pendingShowRerollIcon)
        {
            _pendingShowRerollIcon = false;
            DoShowRerollIcon(_pendingRerollDoubles);
        }

        if (_pendingHideRerollIcon)
        {
            _pendingHideRerollIcon = false;
            DoHideRerollIcon();
        }

        if (_pendingShowNoMovesIcon)
        {
            _pendingShowNoMovesIcon = false;
            DoShowNoMovesIcon();
        }
    }

    private void SetFaceValue(int dieIndex, int value)
    {
        if ((uint)dieIndex >= (uint)_displayDice.Length || _displayDice[dieIndex] == null) return;
        if (value < 1 || value > 6) return;
        _displayDice[dieIndex].Rotation = FaceRotations[value];
    }

    // ── Static API ────────────────────────────────────────────────────────────

    public static void AttachDice(RigidBody3D[] dice)
    {
        _pendingDice           = dice;
        _pendingAttach         = true;
        _pendingHideRerollIcon = true; // player grabbed cup → dismiss reroll icon
    }

    /// <summary>Shows the reroll icon overlaid on the dice HUD panel.</summary>
    public static void ShowRerollIcon(int consecutiveDoubles)
    {
        _pendingRerollDoubles  = consecutiveDoubles;
        _pendingShowRerollIcon = true;
    }

    /// <summary>Shows the no-available-moves exclamation icon overlaid on the dice HUD panel.</summary>
    public static void ShowNoMovesIcon() => _pendingShowNoMovesIcon = true;

    /// <summary>Shows the HUD with dice oriented to the given face values (no live physics tracking).</summary>
    public static void ShowStatic(int die1, int die2)
    {
        _pendingStatic1    = die1;
        _pendingStatic2    = die2;
        _pendingStaticShow = true;
    }

    public static void HideResult() => _pendingHide = true;

    // ── Internal ──────────────────────────────────────────────────────────────

    private void ShowHUD()
    {
        Visible = true;
        _fadeTween?.Kill();
        _fadeTween = CreateTween();
        _fadeTween.TweenProperty(this, "modulate:a", 1f, 0.4f)
                  .SetTrans(Tween.TransitionType.Cubic)
                  .SetEase(Tween.EaseType.Out);
    }

    private void HideHUD()
    {
        if (_rerollIconVisible) return; // keep panel up while reroll icon is showing
        _fadeTween?.Kill();
        _fadeTween = CreateTween();
        _fadeTween.TweenProperty(this, "modulate:a", 0f, 0.5f)
                  .SetTrans(Tween.TransitionType.Cubic)
                  .SetEase(Tween.EaseType.In);
        _fadeTween.TweenCallback(Callable.From(() => {
            Visible = false;
            _noMovesIcon.Modulate = new Color(1f, 1f, 1f, 0f);
            _noMovesIcon.Rotation = 0f;
        }));
    }

    private void DoShowRerollIcon(int consecutiveDoubles)
    {
        string soundPath = consecutiveDoubles switch {
            1 => "res://Assets/Sound FX/reroll_combo_1.wav",
            2 => "res://Assets/Sound FX/reroll_combo_2.wav",
            _ => null,
        };
        if (soundPath != null)
        {
            var clip = GD.Load<AudioStream>(soundPath);
            if (clip != null)
            {
                var sfx = new AudioStreamPlayer { Stream = clip, VolumeDb = -3f };
                AddChild(sfx);
                sfx.Play();
                sfx.Finished += () => sfx.QueueFree();
            }
        }

        // Ensure the panel is visible (may have been fading when extra_turn arrived).
        _fadeTween?.Kill();
        Visible  = true;
        Modulate = new Color(1f, 1f, 1f, 1f);

        // Reset to spawn position before animating.
        _rerollIcon.OffsetTop    =  5f;
        _rerollIcon.OffsetBottom = 69f;
        _rerollIcon.Rotation     =  0f;
        _rerollIcon.Modulate     = new Color(1f, 1f, 1f, 0f);

        _rerollIconVisible = true;
        _rerollTween?.Kill();

        const float dur = 0.7f;
        _rerollTween = _rerollIcon.CreateTween();
        _rerollTween.SetParallel(true);
        _rerollTween.TweenProperty(_rerollIcon, "modulate:a", 1f, dur * 0.6f)
                    .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _rerollTween.TweenProperty(_rerollIcon, "rotation", -Mathf.Tau, dur)
                    .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _rerollTween.TweenProperty(_rerollIcon, "offset_top", -28f, dur)
                    .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _rerollTween.TweenProperty(_rerollIcon, "offset_bottom", 36f, dur)
                    .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
    }

    private void DoHideRerollIcon()
    {
        if (!_rerollIconVisible) return;
        _rerollIconVisible = false;
        _rerollTween?.Kill();
        _rerollTween = _rerollIcon.CreateTween();
        _rerollTween.TweenProperty(_rerollIcon, "modulate:a", 0f, 0.3f)
                    .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        _rerollTween.TweenCallback(Callable.From(() => {
            _rerollIcon.Rotation = 0f;
            HideHUD();
        }));
    }

    private void DoShowNoMovesIcon()
    {
        var clip = GD.Load<AudioStream>("res://Assets/Sound FX/warning_sound.wav");
        if (clip != null)
        {
            var sfx = new AudioStreamPlayer { Stream = clip, VolumeDb = -3f };
            AddChild(sfx);
            sfx.Play();
            sfx.Finished += () => sfx.QueueFree();
        }

        _fadeTween?.Kill();
        Visible  = true;
        Modulate = new Color(1f, 1f, 1f, 1f);

        _noMovesIcon.OffsetTop    =  5f;
        _noMovesIcon.OffsetBottom = 69f;
        _noMovesIcon.Rotation     =  0f;
        _noMovesIcon.Modulate     = new Color(1f, 1f, 1f, 0f);

        _noMovesTween?.Kill();
        const float dur = 0.7f;
        _noMovesTween = _noMovesIcon.CreateTween();
        _noMovesTween.SetParallel(true);
        _noMovesTween.TweenProperty(_noMovesIcon, "modulate:a", 1f, dur * 0.6f)
                     .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _noMovesTween.TweenProperty(_noMovesIcon, "rotation", -Mathf.Tau, dur)
                     .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _noMovesTween.TweenProperty(_noMovesIcon, "offset_top", -28f, dur)
                     .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _noMovesTween.TweenProperty(_noMovesIcon, "offset_bottom", 36f, dur)
                     .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
    }
}
