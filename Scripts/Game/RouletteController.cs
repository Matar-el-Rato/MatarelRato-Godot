using Godot;
using System.Collections.Generic;

public partial class RouletteController : Node3D
{
    [Export] public float SpinnerRps = 0.6f;
    // How fast the spinner decelerates each second (rps/s). At 0.15 it takes ~3 s to stop.
    [Export] public float SpinnerDeceleration = 0.06f;
    [Export] public float BallThrowSpeed = 3.0f;
    [Export] public float LocalGravity = 7f;
    // ball speed (m/s) below which "settled" timer starts
    [Export] public float SettleSpeedThreshold = 0.05f;
    // seconds the ball must stay below threshold to count as settled
    [Export] public float SettleTime = 1.5f;
    // seconds to wait after settling before the next throw
    [Export] public float ResetDelay = 3.0f;
    // hard upper bound on how long the ball rolls before forcing a settle
    [Export] public float MaxBallTime = 6.0f;

    private StaticBody3D _rouletteBody;
    private AnimatableBody3D _spinnerBody;
    private RigidBody3D _ball;
    private AudioStreamPlayer3D _clackPlayer;

    private Vector3 _ballStartPos;
    private Vector3 _prevBallVelocity;
    private float _clackCooldown;
    private float _spinnerAngle;
    private float _spinnerCurrentRps;
    private float _settleTimer;
    private float _resetTimer;
    private float _throwTimer;
    private bool _isSettled;

    public override void _Ready()
    {
        _rouletteBody = GetNode<StaticBody3D>("RouletteBody");
        _spinnerBody  = GetNode<AnimatableBody3D>("SpinnerBody");
        _ball         = GetNode<RigidBody3D>("Ball");

        _ballStartPos = _ball.Position;
        _ball.GravityScale = 0f;

        _spinnerCurrentRps = SpinnerRps;

        // Clack sound — same file the dice use, parented to the ball for 3-D positioning
        _clackPlayer = new AudioStreamPlayer3D
        {
            Stream      = GD.Load<AudioStream>("res://Assets/Sound FX/clack_dice.wav"),
            MaxDistance = 6f,
        };
        _ball.AddChild(_clackPlayer);

        // Build collision shapes at runtime from the actual mesh data
        GenerateTrimeshCollision(_rouletteBody, GetNode("RouletteBody/roulette"));
        GenerateTrimeshCollision(_spinnerBody,  GetNode("SpinnerBody/roulette_spinner")); // trimesh keeps fret dividers
        GenerateBallCollision();

        // Physics materials — tune Friction/Bounce in the Inspector via export or here
        _rouletteBody.PhysicsMaterialOverride = new PhysicsMaterial { Friction = 0.2f, Bounce = 0.3f };
        _spinnerBody.PhysicsMaterialOverride  = new PhysicsMaterial { Friction = 0.4f, Bounce = 0.2f };
        _ball.PhysicsMaterialOverride         = new PhysicsMaterial { Friction = 0.3f, Bounce = 0.4f };

        _ball.LinearDamp     = 0.1f;
        _ball.AngularDamp    = 0.5f;
        _ball.ContinuousCd   = true; // prevent tunnelling through thin roulette surfaces

        BuildCylinderWall(GetNode<Area3D>("ContainmentArea"));

        CallDeferred(nameof(ThrowBall));
    }

    public override void _PhysicsProcess(double delta)
    {
        float f = (float)delta;

        // Gravity is local to the scene so the roulette works upside-down in MainScene.
        // -Basis.Y is the scene's "down" axis in world space.
        _ball.ApplyCentralForce(-GlobalTransform.Basis.Y.Normalized() * LocalGravity * _ball.Mass);

        // If the ball has phased through the geometry its local Y will be wildly out of
        // range (it then accelerates forever under local gravity). Hard-reset immediately.
        float localY = _ball.Position.Y;
        if (localY > 0.6f || localY < -0.4f)
        {
            _settleTimer = 0f;
            _throwTimer  = 0f;
            ResetBall();
            return;
        }

        // Impact sound: a sudden velocity drop means the ball hit something.
        // BodyEntered only fires on first contact so it misses continuous rolling impacts.
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

        // Spin the inner wheel — gradually winds down each throw cycle
        _spinnerCurrentRps    = Mathf.Max(0f, _spinnerCurrentRps - SpinnerDeceleration * f);
        _spinnerAngle        += _spinnerCurrentRps * Mathf.Tau * f;
        _spinnerBody.Rotation = new Vector3(0f, _spinnerAngle, 0f);

        if (_isSettled)
        {
            _resetTimer += f;
            if (_resetTimer >= ResetDelay)
            {
                _isSettled = false;
                _resetTimer = 0f;
                ResetBall();
            }
            return;
        }

        // Hard timeout — force settle after MaxBallTime regardless of velocity
        _throwTimer += f;
        if (_throwTimer >= MaxBallTime)
        {
            Settle();
            return;
        }

        // Check whether the ball has come to rest inside a pocket
        float sqThresh = SettleSpeedThreshold * SettleSpeedThreshold;
        if (_ball.LinearVelocity.LengthSquared()  < sqThresh
         && _ball.AngularVelocity.LengthSquared() < sqThresh)
        {
            _settleTimer += f;
            if (_settleTimer >= SettleTime)
                Settle();
        }
        else
        {
            _settleTimer = 0f;
        }
    }

    private void Settle()
    {
        _isSettled   = true;
        _settleTimer = 0f;
        _throwTimer  = 0f;
        GD.Print("[Roulette] Ball settled — waiting before next throw.");
    }

    private void ThrowBall()
    {
        _spinnerCurrentRps = SpinnerRps; // restart spinner at full speed each cycle
        _throwTimer        = 0f;
        // Throw tangentially along local +Z (perpendicular to the radius at start pos)
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

    // Reads the cylinder dimensions already set by the user in the scene editor, then
    // builds a StaticBody3D trimesh wall with the same size. CylinderShape3D is convex
    // and only blocks from the outside; ConcavePolygonShape3D (trimesh) is double-sided
    // so the inner surface stops the ball from escaping.
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

        // No caps — only the side wall matters; top/bottom are handled by the roulette bowl
        var mesh = new CylinderMesh
        {
            TopRadius      = radius,
            BottomRadius   = radius,
            Height         = height,
            RadialSegments = 48,
            CapTop         = false,
            CapBottom       = false,
        };
        // BackfaceCollision = true makes both sides of every triangle collidable.
        // Without it only outward-facing normals stop objects — the ball is on the
        // inside, so it would pass straight through the default one-sided trimesh.
        var shape = (ConcavePolygonShape3D)mesh.CreateTrimeshShape();
        shape.BackfaceCollision = true;
        wall.AddChild(new CollisionShape3D { Shape = shape });
        wall.PhysicsMaterialOverride = new PhysicsMaterial { Bounce = 0.5f, Friction = 0.1f };
    }

    // ---------------------------------------------------------------------------
    // Collision generation — builds shapes from mesh vertex data at runtime
    // ---------------------------------------------------------------------------

    // Setting col.GlobalTransform after AddChild is unreliable in _Ready() because the
    // transform notification chain hasn't propagated yet. Computing the local transform
    // explicitly avoids that race: body.GlobalTransform.Inverse() * mi.GlobalTransform
    // gives the mesh's position/rotation/scale expressed in body-local space, which is
    // exactly what CollisionShape3D.Transform expects.
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

    // Ball uses a convex hull so its shape + scale are derived the same way as other
    // meshes. Using Mesh.GetAabb() alone ignores any GLB import scale applied to the
    // MeshInstance3D node, which gives a wrongly-sized sphere.
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
            GD.PrintErr("[Roulette] No mesh found for ball — falling back to 1.5 cm sphere.");
        if (count == 0)
            _ball.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.015f } });
    }

    private static List<MeshInstance3D> GetMeshInstances(Node root)
    {
        var result = new List<MeshInstance3D>();
        Collect(root, result);
        return result;
    }

    private static void Collect(Node node, List<MeshInstance3D> list)
    {
        if (node is MeshInstance3D mi)
            list.Add(mi);
        foreach (Node child in node.GetChildren())
            Collect(child, list);
    }
}
