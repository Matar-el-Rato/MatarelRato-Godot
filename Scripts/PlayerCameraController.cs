using Godot;

/// <summary>
/// PlayerCameraController – Godot 4.6 / C#
/// FPS-style controller:
///   • WASD to move horizontally
///   • Space to jump
///   • Mouse to rotate character (Yaw) and Camera (Pitch)
///
/// Required scene tree:
///   CharacterBody3D (this script)
///   ├─ CollisionShape3D
///   ├─ character.glb (Mesh)
///   └─ Camera3D (Camera element)
/// </summary>
public partial class PlayerCameraController : CharacterBody3D
{
	[Export] public float WalkSpeed        = 5.0f;
	[Export] public float SprintSpeed      = 10.0f;
	[Export] public float JumpVelocity     = 6.0f;
	[Export] public float MouseSensitivity = 0.003f;   // rad / px
	[Export] public float GravityMultiplier = 3.0f;    // Slightly higher for better feel

	private Camera3D _camera;
	private float    _gravity;
	private float    _pitch = 0.0f;

	public override void _Ready()
	{
		_camera = GetNode<Camera3D>("Camera3D");
		_gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity");
		Input.MouseMode = Input.MouseModeEnum.Captured;

		// CharacterBody3D floor settings
		FloorSnapLength    = 0.3f; // Increased snap length for better adhesion
		FloorConstantSpeed = true;
		FloorStopOnSlope   = true;
		ApplyFloorSnap();
	}

	// ── Mouse look (Yaw on Body, Pitch on Camera) ───────────────────────────
	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseMotion mm &&
			Input.MouseMode == Input.MouseModeEnum.Captured)
		{
			// YAW  → rotate the entire body so the character model follows.
			RotateY(-mm.Relative.X * MouseSensitivity);

			// PITCH → tilt only the Camera, clamped to ±89°.
			_pitch -= mm.Relative.Y * MouseSensitivity;
			_pitch = Mathf.Clamp(_pitch, Mathf.DegToRad(-89f), Mathf.DegToRad(89f));
			_camera.Rotation = new Vector3(_pitch, 0, 0);
		}

		if (@event.IsActionPressed("ui_cancel"))
		{
			Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
				? Input.MouseModeEnum.Visible
				: Input.MouseModeEnum.Captured;
		}
	}

	// ── Physics ──────────────────────────────────────────────────────────────
	public override void _PhysicsProcess(double delta)
	{
		var vel = Velocity;

		// ── 1. Gravity ────────────────────────────────────────────────────
		if (!IsOnFloor())
		{
			vel.Y -= _gravity * GravityMultiplier * (float)delta;
		}
		else
		{
			// Firmly stick to the ground when not jumping
			// A small negative value ensures IsOnFloor() remains true
			if (vel.Y <= 0)
				vel.Y = -0.1f; 
		}

		// ── 2. Jump (Space) ───────────────────────────────────────────────
		if (Input.IsActionJustPressed("jump") && IsOnFloor())
		{
			vel.Y = JumpVelocity;
		}

		// ── 3. Horizontal movement ────────────────────────────────────────
		var direction = Vector3.Zero;

		if (Input.IsActionPressed("move_forward"))
			direction -= Transform.Basis.Z;

		if (Input.IsActionPressed("move_backward"))
			direction += Transform.Basis.Z;

		if (Input.IsActionPressed("move_left"))
			direction -= Transform.Basis.X;

		if (Input.IsActionPressed("move_right"))
			direction += Transform.Basis.X;

		direction.Y = 0f;

		if (direction.LengthSquared() > 0f)
			direction = direction.Normalized();

		if (direction != Vector3.Zero)
		{
			float speed = Input.IsActionPressed("sprint") ? SprintSpeed : WalkSpeed;
			vel.X = direction.X * speed;
			vel.Z = direction.Z * speed;
		}
		else
		{
			vel.X = Mathf.MoveToward(vel.X, 0f, WalkSpeed);
			vel.Z = Mathf.MoveToward(vel.Z, 0f, WalkSpeed);
		}

		// ── 4. Apply + collide ────────────────────────────────────────────
		Velocity = vel;
		MoveAndSlide();

		// ── 5. Safety net ─────────────────────────────────────────────────
		if (GlobalPosition.Y < -50f) 
		{
			GlobalPosition = new Vector3(0f, 5f, 0f);
			Velocity = Vector3.Zero;
		}
	}
}
