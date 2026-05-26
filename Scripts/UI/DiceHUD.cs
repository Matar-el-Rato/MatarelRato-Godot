using Godot;

/// <summary>
/// Pixelated dice HUD anchored to the bottom-right corner.
///
/// Two separate instances live in the scene:
///   IsRemote=false  — local player's full-size HUD (physics-tracking + icons)
///   IsRemote=true   — remote players' smaller HUD (static faces, auto-hides, closer corner)
///
/// Each die tile is a SubViewportContainer with Stretch=true and StretchShrink=3.
/// Godot renders the inner SubViewport at container_size/3 then scales up 3×
/// with nearest-neighbour filtering — giving uniform chunky pixel blocks.
/// </summary>
public partial class DiceHUD : Control
{
    [Export] public bool IsRemote = false;

    private static DiceHUD _instance;
    private static DiceHUD _remoteInstance;

    private readonly Node3D[] _displayDice = new Node3D[2];
    private Tween _fadeTween;
    private TextureRect _rerollIcon;
    private Tween _rerollTween;
    private bool _rerollIconVisible;

    // Face-axis mapping: +X=4, -X=3, +Y=2, -Y=5, +Z=6, -Z=1.
    private static readonly Vector3[] FaceRotations = new[]
    {
        Vector3.Zero,
        new Vector3( Mathf.Pi / 2f, 0f, 0f),   // 1  (-Z → +Y)
        new Vector3(0f, 0f, 0f),                 // 2  (+Y → +Y)
        new Vector3(0f, 0f, -Mathf.Pi / 2f),    // 3  (-X → +Y)
        new Vector3(0f, 0f,  Mathf.Pi / 2f),    // 4  (+X → +Y)
        new Vector3(Mathf.Pi, 0f, 0f),           // 5  (-Y → +Y)
        new Vector3(-Mathf.Pi / 2f, 0f, 0f),    // 6  (+Z → +Y)
    };

    // ── Local-instance state ──────────────────────────────────────────────────

    private RigidBody3D[] _activeDice;
    private TextureRect   _noMovesIcon;
    private Tween         _noMovesTween;

    private static RigidBody3D[]  _pendingDice;
    private static volatile bool  _pendingAttach;
    private static volatile bool  _pendingHide;
    private static volatile bool  _pendingStaticShow;
    private static int            _pendingStatic1;
    private static int            _pendingStatic2;
    private static volatile bool  _pendingShowRerollIcon;
    private static volatile bool  _pendingHideRerollIcon;
    private static int            _pendingRerollDoubles;
    private static volatile bool  _pendingShowNoMovesIcon;

    // ── Remote-instance state ─────────────────────────────────────────────────

    private Tween _autoHideTween;

    private static volatile bool  _pendingRemoteShow;
    private static int            _pendingRemote1;
    private static int            _pendingRemote2;
    private static volatile bool  _pendingRemoteRerollIcon;
    private static int            _pendingRemoteRerollDoubles;
    private static volatile bool  _pendingRemoteHide;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void _Ready()
    {
        AnchorLeft   = 1f;
        AnchorRight  = 1f;
        AnchorTop    = 1f;
        AnchorBottom = 1f;

        var diceScene = GD.Load<PackedScene>("res://Assets/PlayingSet/General/dice.glb");

        if (IsRemote)
        {
            _remoteInstance = this;
            // Smaller panel docked very close to the corner.
            OffsetRight  = -12f;
            OffsetLeft   = -166f;  // 154 px wide
            OffsetTop    = -88f;   // 76 px tall
            OffsetBottom = -12f;

            BuildLayout(70f, diceScene);
            _rerollIcon = BuildRerollIcon(26f, 52f);
        }
        else
        {
            _instance = this;
            OffsetRight  = -40f;
            OffsetLeft   = -246f;  // 206 px wide
            OffsetTop    = -142f;  // 130 px tall
            OffsetBottom = -12f;

            BuildLayout(110f, diceScene);
            _rerollIcon  = BuildRerollIcon(32f, 64f);
            _noMovesIcon = BuildNoMovesIcon();
        }

        Modulate = new Color(1f, 1f, 1f, 0f);
        Visible  = false;
    }

    private void BuildLayout(float containerSize, PackedScene diceScene)
    {
        var hbox = new HBoxContainer();
        hbox.SetAnchorsPreset(LayoutPreset.FullRect);
        hbox.AddThemeConstantOverride("separation", 8);
        AddChild(hbox);

        for (int i = 0; i < 2; i++)
            BuildDieViewport(i, hbox, diceScene, containerSize);
    }

    private TextureRect BuildRerollIcon(float half, float size)
    {
        var icon = new TextureRect();
        icon.AnchorLeft   = 0.5f;
        icon.AnchorRight  = 0.5f;
        icon.AnchorTop    = 0f;
        icon.AnchorBottom = 0f;
        icon.OffsetLeft   = -half;
        icon.OffsetRight  =  half;
        icon.OffsetTop    =  5f;
        icon.OffsetBottom =  5f + size;
        icon.PivotOffset  = new Vector2(half, half);
        icon.ExpandMode   = TextureRect.ExpandModeEnum.FitWidth;
        icon.StretchMode  = TextureRect.StretchModeEnum.KeepAspectCentered;
        icon.Modulate     = new Color(1f, 1f, 1f, 0f);
        var tex = GD.Load<Texture2D>("res://Assets/Icons/reroll_icon.svg");
        if (tex != null) icon.Texture = tex;
        AddChild(icon);
        return icon;
    }

    private TextureRect BuildNoMovesIcon()
    {
        var icon = new TextureRect();
        icon.AnchorLeft   = 0.5f;
        icon.AnchorRight  = 0.5f;
        icon.AnchorTop    = 0f;
        icon.AnchorBottom = 0f;
        icon.OffsetLeft   = -32f;
        icon.OffsetRight  =  32f;
        icon.OffsetTop    =   5f;
        icon.OffsetBottom =  69f;
        icon.PivotOffset  = new Vector2(32f, 32f);
        icon.ExpandMode   = TextureRect.ExpandModeEnum.FitWidth;
        icon.StretchMode  = TextureRect.StretchModeEnum.KeepAspectCentered;
        icon.Modulate     = new Color(1f, 1f, 1f, 0f);
        var tex = GD.Load<Texture2D>("res://Assets/Icons/exclamation.svg");
        if (tex != null) icon.Texture = tex;
        AddChild(icon);
        return icon;
    }

    private void BuildDieViewport(int index, HBoxContainer row, PackedScene diceScene, float containerSize)
    {
        var container = new SubViewportContainer();
        container.CustomMinimumSize   = new Vector2(containerSize, containerSize);
        container.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        container.Stretch             = true;
        container.StretchShrink       = 3;
        container.TextureFilter       = TextureFilterEnum.Nearest;
        row.AddChild(container);

        var viewport = new SubViewport();
        viewport.OwnWorld3D             = true;
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
        if (IsRemote)
            ProcessRemote();
        else
            ProcessLocal();
    }

    private void ProcessRemote()
    {
        if (_pendingRemoteHide)
        {
            _pendingRemoteHide      = false;
            _autoHideTween?.Kill();
            _rerollTween?.Kill();
            _rerollIconVisible      = false;
            _rerollIcon.Modulate    = new Color(1f, 1f, 1f, 0f);
            HideHUD();
        }

        if (_pendingRemoteShow)
        {
            _pendingRemoteShow   = false;
            _autoHideTween?.Kill();
            _rerollTween?.Kill();
            _rerollIconVisible   = false;
            _rerollIcon.Modulate = new Color(1f, 1f, 1f, 0f);
            SetFaceValue(0, _pendingRemote1);
            SetFaceValue(1, _pendingRemote2);
            ShowHUD();
            _autoHideTween = CreateTween();
            _autoHideTween.TweenInterval(3.5f);
            _autoHideTween.TweenCallback(Callable.From(() => HideHUD()));
        }

        if (_pendingRemoteRerollIcon)
        {
            _pendingRemoteRerollIcon = false;
            DoShowRerollIcon(_pendingRemoteRerollDoubles);
        }
    }

    private void ProcessLocal()
    {
        // Hide processed first so a same-frame show wins over a stale hide.
        if (_pendingHide)
        {
            _pendingHide = false;
            _activeDice  = null;
            HideHUD();
        }

        if (_pendingAttach)
        {
            _pendingAttach = false;
            _activeDice    = _pendingDice;
            ShowHUD();
        }

        if (_pendingStaticShow)
        {
            _pendingStaticShow = false;
            _activeDice        = null;
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

    // ── Static API — local HUD ────────────────────────────────────────────────

    public static void AttachDice(RigidBody3D[] dice)
    {
        _pendingDice           = dice;
        _pendingAttach         = true;
        _pendingHideRerollIcon = true; // player grabbed cup → dismiss reroll icon
    }

    public static void ShowRerollIcon(int consecutiveDoubles)
    {
        _pendingRerollDoubles  = consecutiveDoubles;
        _pendingShowRerollIcon = true;
    }

    public static void ShowNoMovesIcon() => _pendingShowNoMovesIcon = true;

    public static void ShowStatic(int die1, int die2)
    {
        _pendingStatic1    = die1;
        _pendingStatic2    = die2;
        _pendingStaticShow = true;
    }

    public static void HideResult() => _pendingHide = true;

    // ── Static API — remote HUD ───────────────────────────────────────────────

    public static void ShowRemote(int die1, int die2)
    {
        _pendingRemote1    = die1;
        _pendingRemote2    = die2;
        _pendingRemoteShow = true;
    }

    public static void ShowRemoteRerollIcon(int consecutiveDoubles)
    {
        _pendingRemoteRerollDoubles = consecutiveDoubles;
        _pendingRemoteRerollIcon    = true;
    }

    public static void HideRemote() => _pendingRemoteHide = true;

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
            if (_noMovesIcon != null)
            {
                _noMovesIcon.Modulate = new Color(1f, 1f, 1f, 0f);
                _noMovesIcon.Rotation = 0f;
            }
        }));
    }

    private void DoShowRerollIcon(int consecutiveDoubles)
    {
        if (!IsRemote)
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
        }

        _fadeTween?.Kill();
        Visible  = true;
        Modulate = new Color(1f, 1f, 1f, 1f);

        float half      = IsRemote ? 26f : 32f;
        float size      = IsRemote ? 52f : 64f;
        float floatDist = IsRemote ? 22f : 33f;

        _rerollIcon.OffsetTop    =  5f;
        _rerollIcon.OffsetBottom =  5f + size;
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
        _rerollTween.TweenProperty(_rerollIcon, "offset_top", 5f - floatDist, dur)
                    .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _rerollTween.TweenProperty(_rerollIcon, "offset_bottom", 5f + size - floatDist, dur)
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
