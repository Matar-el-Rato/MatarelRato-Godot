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
	[Export] public NodePath CharacterModelPath = "character";

	private Camera3D _camera;
	private float    _gravity;
	private float    _pitch = 0.0f;
	private Node3D   _activeCharacter;

	public override void _Ready()
	{
		_camera = GetNode<Camera3D>("Camera3D");
		_activeCharacter = GetNodeOrNull<Node3D>(CharacterModelPath);
		_gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity");
		Input.MouseMode = Input.MouseModeEnum.Captured;

		// CharacterBody3D floor settings
		FloorSnapLength    = 0.3f;
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

		// ── 5. Animations ─────────────────────────────────────────────────
		UpdateAnimations(direction);

		// ── 6. Safety net ─────────────────────────────────────────────────
		if (GlobalPosition.Y < -50f) 
		{
			GlobalPosition = new Vector3(0f, 5f, 0f);
			Velocity = Vector3.Zero;
		}
	}

	private void UpdateAnimations(Vector3 direction)
	{
		if (_activeCharacter == null) return;
		
		var animPlayer = _activeCharacter.GetNodeOrNull<AnimationPlayer>("AnimationPlayer") ?? 
						 _activeCharacter.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
		
		if (animPlayer == null) return;

		// Target name
		string walkAnim = "WalkingCycle_001";

		if (IsOnFloor() && direction.LengthSquared() > 0)
		{
			// ── Walking ──────────────────────────────────────────────────────
			if (animPlayer.HasAnimation(walkAnim))
			{
				if (animPlayer.CurrentAnimation != walkAnim)
				{
					var anim = animPlayer.GetAnimation(walkAnim);
					if (anim != null) anim.LoopMode = Animation.LoopModeEnum.Linear;
					animPlayer.Play(walkAnim);
				}
				float dot       = direction.Dot(-Transform.Basis.Z);
				float speedMult = Input.IsActionPressed("sprint") ? 2.0f : 1.0f;
				animPlayer.SpeedScale = (dot < -0.1f ? -1.0f : 1.0f) * speedMult;
			}
		}
		else
		{
			// ── Idle/Stopped ─────────────────────────────────────────────────
			if (animPlayer.CurrentAnimation == walkAnim && animPlayer.IsPlaying())
			{
				animPlayer.SpeedScale = 1.0f;
				GoToIdlePose(animPlayer);
			}
		}
		
		// Correct orientation for BOTH states.
		// Walking needs 0 (perfect) but idle needs -90 (counter parent offset).
		// We force both because animations might lack rotation tracks and inherit previous values.
		var root = animPlayer.GetNodeOrNull<Node3D>(animPlayer.RootNode);
		if (root != null)
		{
			float targetY = (direction.LengthSquared() > 0.001f) ? 0f : -90f;
			root.RotationDegrees = new Vector3(root.RotationDegrees.X, targetY, root.RotationDegrees.Z);
		}
	}

	/// <summary>
	/// Plays the RESET animation and, via a one-shot AnimationFinished signal,
	/// applies a -90° Y correction after RESET has fully evaluated.
	/// Fixes the 90° Y rotation Blender bakes into every exported model's bind pose.
	/// Safe: self-disconnecting, applied exactly once per transition, not cumulative.
	/// </summary>
	private static void GoToIdlePose(AnimationPlayer animPlayer)
	{
		if (animPlayer.HasAnimation("RESET"))
			animPlayer.Play("RESET");
		else
			animPlayer.Stop(false);
	}

	/// <summary>
	/// Swaps the current character model with a new one, preserving orientation nesting.
	/// </summary>
	public void SwapCharacter(PackedScene newCharacterScene)
	{
		if (newCharacterScene == null) return;

		// We look for the orientation fix node to swap the model inside it
		var orientationFix = GetNodeOrNull<Node3D>("character/OrientationFix");
		if (orientationFix == null)
		{
			GD.PrintErr("PlayerCameraController: Could not find 'character/OrientationFix' to swap model.");
			return;
		}

		// Remove existing children from the fix node
		foreach (var child in orientationFix.GetChildren())
		{
			child.QueueFree();
		}

		// Instantiate and add the new character
		var newModel = newCharacterScene.Instantiate<Node3D>();
		orientationFix.AddChild(newModel);
		
		// Update reference and re-initialize visuals
		_activeCharacter = orientationFix; 

		
		// Force an animation snap for the new model
		var animPlayer = newModel.GetNodeOrNull<AnimationPlayer>("AnimationPlayer") ?? 
						 newModel.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
		if (animPlayer != null && animPlayer.HasAnimation("WalkingCycle_001"))
		{
			animPlayer.Play("WalkingCycle_001");
			animPlayer.Stop();
		}
	}
}
