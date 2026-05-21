using Godot;
using System.Collections.Generic;

public partial class RouletteController : Node3D
{
    [Export] public float SpinnerRps           = 0.6f;
    [Export] public float SpinnerDeceleration  = 0.06f;
    [Export] public float BallThrowSpeed       = 3.0f;
    [Export] public float LocalGravity         = 7f;
    [Export] public float SettleSpeedThreshold = 0.05f;
    [Export] public float SettleTime           = 1.5f;
    [Export] public float MaxBallTime          = 6.0f;
    [Export] public float RevealDuration = 0.55f;
    [Export] public float HideDuration  = 0.4f;

    [Signal] public delegate void BallSettledEventHandler();

    private StaticBody3D      _rouletteBody;
    private AnimatableBody3D  _spinnerBody;
    private RigidBody3D       _ball;
    private AudioStreamPlayer3D _clackPlayer;

    private Vector3 _ballStartPos;
    private Vector3 _prevBallVelocity;
    private float   _clackCooldown;
    private float   _spinnerAngle;
    private float   _spinnerCurrentRps;
    private float   _settleTimer;
    private float   _throwTimer;
    private bool    _isSettled;
    private bool    _active;

    public override void _Ready()
    {
        _rouletteBody = GetNode<StaticBody3D>("RouletteBody");
        _spinnerBody  = GetNode<AnimatableBody3D>("SpinnerBody");
        _ball         = GetNode<RigidBody3D>("Ball");

        _ballStartPos = _ball.Position;
        _ball.GravityScale = 0f;

        _clackPlayer = new AudioStreamPlayer3D
        {
            Stream      = GD.Load<AudioStream>("res://Assets/Sound FX/clack_dice.wav"),
            MaxDistance = 6f,
        };
        _ball.AddChild(_clackPlayer);

        GenerateTrimeshCollision(_rouletteBody, GetNode("RouletteBody/roulette"));
        GenerateTrimeshCollision(_spinnerBody,  GetNode("SpinnerBody/roulette_spinner"));
        GenerateBallCollision();

        _rouletteBody.PhysicsMaterialOverride = new PhysicsMaterial { Friction = 0.2f, Bounce = 0.3f };
        _spinnerBody.PhysicsMaterialOverride  = new PhysicsMaterial { Friction = 0.4f, Bounce = 0.2f };
        _ball.PhysicsMaterialOverride         = new PhysicsMaterial { Friction = 0.3f, Bounce = 0.4f };

        _ball.LinearDamp   = 0.1f;
        _ball.AngularDamp  = 0.5f;
        _ball.ContinuousCd = true;

        BuildCylinderWall(GetNode<Area3D>("ContainmentArea"));

        // Start hidden — scale pop-in/out is used for reveal/hide to avoid
        // moving physics bodies (StaticBody3D/RigidBody3D don't reliably follow
        // a parent Node3D position tween in Godot's physics server).
        SetCollisionsEnabled(false);
        Visible = false;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!_active) return;

        float f = (float)delta;

        _ball.ApplyCentralForce(-GlobalTransform.Basis.Y.Normalized() * LocalGravity * _ball.Mass);

        float localY = _ball.Position.Y;
        if (localY > 0.6f || localY < -0.4f)
        {
            _settleTimer = 0f;
            _throwTimer  = 0f;
            ResetBall();
            return;
        }

        _clackCooldown = Mathf.Max(0f, _clackCooldown - f);
        float velDelta = (_prevBallVelocity - _ball.LinearVelocity).Length();
        if (velDelta > 0.4f && _clackCooldown <= 0f && _clackPlayer.IsInsideTree())
        {
            float intensity         = Mathf.Clamp(velDelta / 5f, 0f, 1f);
            _clackPlayer.PitchScale = (float)GD.RandRange(0.9, 1.1);
            _clackPlayer.VolumeDb   = Mathf.LinearToDb(intensity) - 4f;
            _clackPlayer.Play();
            _clackCooldown = 0.12f;
        }
        _prevBallVelocity = _ball.LinearVelocity;

        _spinnerCurrentRps  = Mathf.Max(0f, _spinnerCurrentRps - SpinnerDeceleration * f);
        _spinnerAngle      += _spinnerCurrentRps * Mathf.Tau * f;
        _spinnerBody.Rotation = new Vector3(0f, _spinnerAngle, 0f);

        _throwTimer += f;
        if (_throwTimer >= MaxBallTime) { Settle(); return; }

        float sqThresh = SettleSpeedThreshold * SettleSpeedThreshold;
        if (_ball.LinearVelocity.LengthSquared()  < sqThresh
         && _ball.AngularVelocity.LengthSquared() < sqThresh)
        {
            _settleTimer += f;
            if (_settleTimer >= SettleTime) Settle();
        }
        else
        {
            _settleTimer = 0f;
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Scale the roulette in from near-zero. Collisions are re-enabled once full size.</summary>
    public void Reveal()
    {
        Scale   = new Vector3(0.001f, 0.001f, 0.001f);
        Visible = true;

        var t = CreateTween();
        t.TweenProperty(this, "scale", Vector3.One, RevealDuration)
         .SetTrans(Tween.TransitionType.Back)
         .SetEase(Tween.EaseType.Out);
        t.TweenCallback(Callable.From(() => SetCollisionsEnabled(true)));
    }

    /// <summary>Scale the roulette out, hide it, and reset state for next use.</summary>
    public void HideAgain()
    {
        _active               = false;
        _ball.LinearVelocity  = Vector3.Zero;
        _ball.AngularVelocity = Vector3.Zero;
        SetCollisionsEnabled(false);

        var t = CreateTween();
        t.TweenProperty(this, "scale", new Vector3(0.001f, 0.001f, 0.001f), HideDuration)
         .SetTrans(Tween.TransitionType.Cubic)
         .SetEase(Tween.EaseType.In);
        t.TweenCallback(Callable.From(() =>
        {
            Visible            = false;
            Scale              = Vector3.One;
            _ball.Position     = _ballStartPos;
            _spinnerCurrentRps = 0f;
            _isSettled         = false;
            _settleTimer       = 0f;
            _throwTimer        = 0f;
        }));
    }

    private void SetCollisionsEnabled(bool enabled)
    {
        var mode = enabled ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        SetCollisionsEnabled(this, mode);
    }

    private static void SetCollisionsEnabled(Node node, ProcessModeEnum mode)
    {
        if (node is CollisionObject3D col) col.ProcessMode = mode;
        foreach (Node child in node.GetChildren(true))
            SetCollisionsEnabled(child, mode);
    }

    /// <summary>Start the spinner and throw the ball. Call after Reveal has completed.</summary>
    public void BeginSpin()
    {
        _isSettled         = false;
        _settleTimer       = 0f;
        _throwTimer        = 0f;
        _spinnerCurrentRps = SpinnerRps;
        _active            = true;
        ThrowBall();
    }

    /// <summary>Stop physics, reset ball to start, prepare for next use.</summary>
    public void ResetAndStop()
    {
        _active            = false;
        _isSettled         = false;
        _spinnerCurrentRps = 0f;
        _settleTimer       = 0f;
        _throwTimer        = 0f;
        _ball.LinearVelocity  = Vector3.Zero;
        _ball.AngularVelocity = Vector3.Zero;
        _ball.Position        = _ballStartPos;
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private void Settle()
    {
        _active      = false;
        _isSettled   = true;
        _settleTimer = 0f;
        _throwTimer  = 0f;
        GD.Print("[Roulette] Ball settled.");
        EmitSignal(SignalName.BallSettled);
    }

    private void ThrowBall()
    {
        _throwTimer = 0f;
        var dir = (GlobalTransform.Basis * new Vector3(0f, 0f, 1f)).Normalized();
        _ball.LinearVelocity  = dir * BallThrowSpeed;
        _ball.AngularVelocity = Vector3.Zero;
    }

    private void ResetBall()
    {
        _ball.LinearVelocity  = Vector3.Zero;
        _ball.AngularVelocity = Vector3.Zero;
        _ball.Position        = _ballStartPos;
        CallDeferred(nameof(ThrowBall));
    }

    private void BuildCylinderWall(Area3D containmentArea)
    {
        float radius = 0.72f, height = 0.7f;
        if (containmentArea.GetChildOrNull<CollisionShape3D>(0)?.Shape is CylinderShape3D cyl)
        {
            radius = cyl.Radius;
            height = cyl.Height;
        }

        var wall = new StaticBody3D { Position = containmentArea.Position };
        AddChild(wall);

        var mesh = new CylinderMesh
        {
            TopRadius      = radius,
            BottomRadius   = radius,
            Height         = height,
            RadialSegments = 48,
            CapTop         = false,
            CapBottom      = false,
        };
        var shape = (ConcavePolygonShape3D)mesh.CreateTrimeshShape();
        shape.BackfaceCollision = true;
        wall.AddChild(new CollisionShape3D { Shape = shape });
        wall.PhysicsMaterialOverride = new PhysicsMaterial { Bounce = 0.5f, Friction = 0.1f };
    }

    private void GenerateTrimeshCollision(PhysicsBody3D body, Node meshRoot)
    {
        int count = 0;
        foreach (MeshInstance3D mi in GetMeshInstances(meshRoot))
        {
            if (mi.Mesh == null) continue;
            var col = new CollisionShape3D { Shape = mi.Mesh.CreateTrimeshShape() };
            body.AddChild(col);
            col.Transform = body.GlobalTransform.Inverse() * mi.GlobalTransform;
            count++;
        }
        if (count == 0)
            GD.PrintErr($"[Roulette] No mesh instances found under {meshRoot.Name} — collision shape missing!");
    }

    private void GenerateBallCollision()
    {
        int count = 0;
        foreach (MeshInstance3D mi in GetMeshInstances(GetNode("Ball/roulette_ball")))
        {
            if (mi.Mesh == null) continue;
            var col = new CollisionShape3D { Shape = mi.Mesh.CreateConvexShape() };
            _ball.AddChild(col);
            col.Transform = _ball.GlobalTransform.Inverse() * mi.GlobalTransform;
            count++;
        }
        if (count == 0)
        {
            GD.PrintErr("[Roulette] No mesh found for ball — falling back to 1.5 cm sphere.");
            _ball.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.015f } });
        }
    }

    private static List<MeshInstance3D> GetMeshInstances(Node root)
    {
        var result = new List<MeshInstance3D>();
        Collect(root, result);
        return result;
    }

    private static void Collect(Node node, List<MeshInstance3D> list)
    {
        if (node is MeshInstance3D mi) list.Add(mi);
        foreach (Node child in node.GetChildren()) Collect(child, list);
    }
}
