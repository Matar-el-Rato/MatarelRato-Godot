using Godot;

public partial class DamoclesSword : Node3D
{
    [Export] public float HangDistance    = 0.9f;
    [Export] public float SpringStiffness = 12f;
    [Export] public float SpringDamping   = 5f;
    [Export] public float Gravity         = 9.8f;
    [Export] public float ImpaleDepth     = 0.05f;
    [Export] public float RopeRadius      = 0.004f;

    [ExportGroup("Timer FX")]
    [Export] public AudioStream ClockTickSound;
    [Export] public float TickVolumeStart  = -20f;
    [Export] public float TickVolume20     = -14f;
    [Export] public float TickVolume10     =  -8f;
    [Export] public float TickVolume5      =  -2f;
    [Export] public float Stage20DropExtra =  0.18f;
    [Export] public float GlowRange        =  0.7f;
    [Export] public float GlowEnergy10     =  2.5f;
    [Export] public float GlowEnergy5      =  5.0f;
    [Export] public float ShiverAmplitude  =  0.003f;
    [Export] public float ShiverFrequency  =  10f;    // Hz

    /// <summary>Fired (once) the frame the blade sinks into the tablero.</summary>
    public event System.Action Impaled;

    private enum State { Hidden, Hanging, Falling, Impaled }

    private State          _state         = State.Hidden;
    private Node3D         _ophanimNode;
    private float          _tableroWorldY;
    private Vector3        _springVelocity;
    private Vector3        _fallVelocity;
    private MeshInstance3D _rope;
    private float          _hangDistanceBase;

    // timer FX
    private AudioStreamPlayer3D _tickPlayer;
    private OmniLight3D         _glowLight;
    private Tween               _glowTween;
    private float _tickAccum  = 0f;
    private float _shiverTime = 0f;
    private bool  _shivering  = false;

    public override void _Ready()
    {
        _hangDistanceBase = HangDistance;
        Visible = false;
        SetProcess(false);

        // Rope mesh
        _rope = new MeshInstance3D { TopLevel = true, Visible = false };
        _rope.Mesh = new CylinderMesh { Height = 1f, TopRadius = RopeRadius, BottomRadius = RopeRadius };
        _rope.MaterialOverride = new StandardMaterial3D
        {
            ShadingMode = StandardMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(0.28f, 0.18f, 0.08f),
        };
        AddChild(_rope);

        // Tick audio — load from known path if not assigned via export
        if (ClockTickSound == null)
            ClockTickSound = GD.Load<AudioStream>("res://Assets/Sound FX/clock_tick.wav");

        _tickPlayer = new AudioStreamPlayer3D
        {
            Stream   = ClockTickSound,
            VolumeDb = TickVolumeStart,
            UnitSize = 3f,
            MaxDb    = 6f,
        };
        AddChild(_tickPlayer);

        // Glow point light (hidden until 10s warning)
        _glowLight = new OmniLight3D
        {
            Visible     = false,
            LightColor  = new Color(1f, 0.12f, 0.08f),
            LightEnergy = 0f,
            OmniRange   = GlowRange,
        };
        AddChild(_glowLight);

        FocusController.FocusStateChanged += OnFocusStateChanged;
    }

    public override void _ExitTree()
    {
        FocusController.FocusStateChanged -= OnFocusStateChanged;
    }

    private void OnFocusStateChanged(bool focused)
    {
        if (focused)
        {
            Visible       = false;
            _rope.Visible = false;
        }
        else
        {
            Visible       = _state != State.Hidden;
            _rope.Visible = _state == State.Hanging;
        }
    }

    public void SpawnFromOphanim(Node3D ophanimNode, float tableroWorldY)
    {
        _ophanimNode    = ophanimNode;
        _tableroWorldY  = tableroWorldY;
        _springVelocity = Vector3.Zero;
        GlobalPosition  = ophanimNode.GlobalPosition;
        Visible         = true;
        _rope.Visible   = true;
        _state          = State.Hanging;

        // Reset timer FX
        HangDistance  = _hangDistanceBase;
        _tickAccum    = 0f;
        _shivering    = false;
        _shiverTime   = 0f;
        if (_tickPlayer != null) _tickPlayer.VolumeDb = TickVolumeStart;
        _glowTween?.Kill();
        _glowTween = null;
        if (_glowLight != null)
        {
            _glowLight.Visible     = false;
            _glowLight.LightEnergy = 0f;
            _glowLight.LightColor  = new Color(1f, 0.12f, 0.08f);
        }

        SetProcess(true);
    }

    /// <summary>Called by TableManager when a turn_timer_warning arrives.</summary>
    public void OnTimerWarning(int secondsRemaining)
    {
        if (_state != State.Hanging) return;

        _glowTween?.Kill();
        _glowTween = null;

        if (secondsRemaining == 20)
        {
            // Sword hangs a bit lower over 3 s
            var tween = CreateTween();
            tween.TweenMethod(
                Callable.From((float v) => HangDistance = v),
                HangDistance, HangDistance + Stage20DropExtra, 3f)
                .SetTrans(Tween.TransitionType.Cubic)
                .SetEase(Tween.EaseType.Out);

            // Volume ramps up to 20-s level over 5 s
            if (_tickPlayer != null)
            {
                var vt = CreateTween();
                vt.TweenProperty(_tickPlayer, "volume_db", TickVolume20, 5f)
                  .SetTrans(Tween.TransitionType.Linear);
            }
        }
        else if (secondsRemaining == 10)
        {
            // Red glow fades in
            if (_glowLight != null)
            {
                _glowLight.Visible    = true;
                _glowLight.LightColor = new Color(1f, 0.12f, 0.08f);
                _glowTween = CreateTween();
                _glowTween.TweenProperty(_glowLight, "light_energy", GlowEnergy10, 2f)
                    .SetTrans(Tween.TransitionType.Quad)
                    .SetEase(Tween.EaseType.Out);
            }
            if (_tickPlayer != null)
            {
                var vt = CreateTween();
                vt.TweenProperty(_tickPlayer, "volume_db", TickVolume10, 4f)
                  .SetTrans(Tween.TransitionType.Linear);
            }
        }
        else if (secondsRemaining == 5)
        {
            // Glow shifts red → yellow and brightens; sword starts shivering
            if (_glowLight != null)
            {
                _glowTween = CreateTween().SetParallel(true);
                _glowTween.TweenProperty(_glowLight, "light_color",  new Color(1f, 0.85f, 0.1f), 5f)
                    .SetTrans(Tween.TransitionType.Quad)
                    .SetEase(Tween.EaseType.InOut);
                _glowTween.TweenProperty(_glowLight, "light_energy", GlowEnergy5, 2f)
                    .SetTrans(Tween.TransitionType.Quad)
                    .SetEase(Tween.EaseType.Out);
            }
            _shivering  = true;
            _shiverTime = 0f;
            if (_tickPlayer != null)
            {
                var vt = CreateTween();
                vt.TweenProperty(_tickPlayer, "volume_db", TickVolume5, 2f)
                  .SetTrans(Tween.TransitionType.Linear);
            }
        }
    }

    public void Cut()
    {
        if (_state != State.Hanging) return;
        _fallVelocity = _springVelocity;
        _rope.Visible = false;
        _shivering    = false;
        _glowTween?.Kill();
        _glowTween = null;
        if (_glowLight  != null) _glowLight.Visible = false;
        if (_tickPlayer != null) _tickPlayer.Stop();
        _state = State.Falling;
    }

    public void Dismiss()
    {
        Visible         = false;
        _rope.Visible   = false;
        _state          = State.Hidden;
        _springVelocity = Vector3.Zero;
        _fallVelocity   = Vector3.Zero;
        _shivering      = false;
        _glowTween?.Kill();
        _glowTween = null;
        if (_tickPlayer != null) _tickPlayer.Stop();
        if (_glowLight  != null) { _glowLight.Visible = false; _glowLight.LightEnergy = 0f; }
        SetProcess(false);
    }

    private void UpdateRope()
    {
        if (_ophanimNode == null || !IsInstanceValid(_ophanimNode)) return;

        var   from = _ophanimNode.GlobalPosition;
        var   to   = GlobalPosition + Vector3.Up * 0.1f;
        var   diff = to - from;
        float dist = diff.Length();
        if (dist < 0.01f) return;

        var dir   = diff / dist;
        var refUp = Mathf.Abs(dir.Dot(Vector3.Up)) < 0.9f ? Vector3.Up : Vector3.Right;
        var right = dir.Cross(refUp).Normalized();
        var back  = right.Cross(dir).Normalized();

        _rope.GlobalTransform = new Transform3D(new Basis(right, dir * dist, back), (from + to) * 0.5f);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        bool focusHide = FocusController.Instance?.IsFocused ?? false;
        Visible       = !focusHide;
        _rope.Visible = !focusHide && _state == State.Hanging;
        if (focusHide) return;

        if (_state == State.Hanging)
        {
            if (_ophanimNode == null || !IsInstanceValid(_ophanimNode)) return;

            // Spring physics
            var target = _ophanimNode.GlobalPosition - Vector3.Up * HangDistance;
            var force  = (target - GlobalPosition) * SpringStiffness - _springVelocity * SpringDamping;
            _springVelocity += force * dt;
            GlobalPosition  += _springVelocity * dt;

            // Shiver overlay (small high-freq jitter, spring corrects the drift)
            if (_shivering)
            {
                _shiverTime += dt;
                float phase = _shiverTime * ShiverFrequency * Mathf.Tau;
                GlobalPosition += new Vector3(
                    Mathf.Sin(phase)           * ShiverAmplitude,
                    Mathf.Sin(phase * 1.5f)    * ShiverAmplitude * 0.3f,
                    Mathf.Cos(phase * 0.85f)   * ShiverAmplitude * 0.6f
                );
            }

            UpdateRope();

            // Clock tick — one per second
            _tickAccum += dt;
            if (_tickAccum >= 1f)
            {
                _tickAccum -= 1f;
                if (ClockTickSound != null && _tickPlayer != null)
                    _tickPlayer.Play();
            }
        }
        else if (_state == State.Falling)
        {
            _fallVelocity.Y -= Gravity * dt;
            GlobalPosition  += _fallVelocity * dt;

            if (GlobalPosition.Y <= _tableroWorldY - ImpaleDepth)
            {
                var p = GlobalPosition;
                p.Y            = _tableroWorldY - ImpaleDepth;
                GlobalPosition = p;
                _state         = State.Impaled;
                SetProcess(false);
                Impaled?.Invoke();
            }
        }
    }
}
