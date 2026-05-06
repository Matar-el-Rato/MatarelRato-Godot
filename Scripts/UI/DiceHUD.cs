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

    private RigidBody3D[] _activeDice;
    private Tween         _fadeTween;

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
    }

    // ── Static API ────────────────────────────────────────────────────────────

    public static void AttachDice(RigidBody3D[] dice)
    {
        _pendingDice   = dice;
        _pendingAttach = true;
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
        _fadeTween?.Kill();
        _fadeTween = CreateTween();
        _fadeTween.TweenProperty(this, "modulate:a", 0f, 0.5f)
                  .SetTrans(Tween.TransitionType.Cubic)
                  .SetEase(Tween.EaseType.In);
        _fadeTween.TweenCallback(Callable.From(() => Visible = false));
    }
}
