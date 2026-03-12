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
	[Export] public bool MovementEnabled = true;

	private Camera3D _camera;
	private CollisionShape3D _collisionShape;
	private float    _gravity;
	private float    _pitch = 0.0f;
	private Node3D   _activeCharacter;
	[Export] private CharacterEntry _activeEntry;
	private Vector3  _baseCameraPos;
	private float    _baseFOV;

	private bool     _isSitting = false;
	private bool     _isTransitioning = false;
	private Chair    _currentChair;
	private float    _sittingYaw = 0f;
	private Vector3  _preSitPosition;
	private const float SITTING_YAW_LIMIT = 1.2f; // ~70 degrees

	public override void _Ready()
	{
		EnsureInitialized();

		// CharacterBody3D floor settings
		FloorSnapLength    = 0.3f;
		FloorConstantSpeed = true;
		FloorStopOnSlope   = true;
		ApplyFloorSnap();
	}

	private void EnsureInitialized()
	{
		if (_camera == null)
		{
			_camera = GetNode<Camera3D>("Camera3D");
			_baseCameraPos = _camera.Position;
			_baseFOV = _camera.Fov;
		}
		if (_collisionShape == null)
		{
			_collisionShape = GetNodeOrNull<CollisionShape3D>("CollisionShape3D");
		}
		if (_activeCharacter == null)
		{
			_activeCharacter = GetNodeOrNull<Node3D>(CharacterModelPath);
		}
		_gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity");
		Input.MouseMode = Input.MouseModeEnum.Captured;
	}



	// ── Mouse look (Yaw on Body, Pitch on Camera) ───────────────────────────
	public override void _UnhandledInput(InputEvent @event)
	{
		if (!MovementEnabled || _isTransitioning) return;

		if (@event is InputEventMouseMotion mm &&
			Input.MouseMode == Input.MouseModeEnum.Captured)
		{
			if (_isSitting)
			{
				// Clamp YAW when sitting - apply to CAMERA only to keep body still
				_sittingYaw = Mathf.Clamp(_sittingYaw - mm.Relative.X * MouseSensitivity, -SITTING_YAW_LIMIT, SITTING_YAW_LIMIT);
			}
			else
			{
				// YAW  → rotate the entire body so the character model follows.
				RotateY(-mm.Relative.X * MouseSensitivity);
			}

			// PITCH → tilt only the Camera, clamped to ±89°.
			_pitch -= mm.Relative.Y * MouseSensitivity;
			_pitch = Mathf.Clamp(_pitch, Mathf.DegToRad(-89f), Mathf.DegToRad(89f));
			
			if (_isSitting)
				_camera.Rotation = new Vector3(_pitch, _sittingYaw, 0);
			else
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
		if (MovementEnabled && Input.IsActionJustPressed("jump") && IsOnFloor())
		{
			vel.Y = JumpVelocity;
		}

		// ── 3. Horizontal movement ────────────────────────────────────────
		var direction = Vector3.Zero;

		if (MovementEnabled && !_isSitting)
		{
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
		}
		else
		{
			vel.X = Mathf.MoveToward(vel.X, 0f, WalkSpeed);
			vel.Z = Mathf.MoveToward(vel.Z, 0f, WalkSpeed);

			if (_isSitting && (Input.IsActionJustPressed("sprint") || Input.IsKeyPressed(Key.Shift))) // Use Shift to get up
			{
				Unsit();
			}

			// Force camera position while sitting (overrides animation tracks)
			if (_isSitting && _activeEntry != null && !_isTransitioning)
			{
				_camera.Position = _baseCameraPos + _activeEntry.CameraOffset + _activeEntry.SittingCameraOffset;
			}
		}

		// ── 4. Apply + collide ────────────────────────────────────────────
		Velocity = vel;
		if (!_isSitting && !_isTransitioning)
			MoveAndSlide();
		else if (_isSitting && !_isTransitioning)
			GlobalPosition = GlobalPosition; // Stay put

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

		// Target names
		string walkAnim = "WalkingCycle_001";
		string sitAnim = "sittingidle_001";

		if (_isSitting)
		{
			if (animPlayer.HasAnimation(sitAnim))
			{
				if (animPlayer.CurrentAnimation != sitAnim)
				{
					var anim = animPlayer.GetAnimation(sitAnim);
					if (anim != null) anim.LoopMode = Animation.LoopModeEnum.Linear;
					animPlayer.Play(sitAnim);
				}
			}
			return; // Don't apply further rotation fix while sitting
		}

		// If we are NOT sitting, we must NOT be playing the sit animation.
		if (animPlayer.CurrentAnimation == sitAnim)
		{
			animPlayer.Stop();
			if (animPlayer.HasAnimation(walkAnim))
			{
				animPlayer.Play(walkAnim);
				animPlayer.Stop(true); // Snap to the first frame (standing)
			}
			GoToIdlePose(animPlayer);
		}

		if (direction.LengthSquared() > 0.001f)
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
			float idleRot = _activeEntry?.IdleRotation ?? 0f;
			float targetY = (direction.LengthSquared() > 0.001f) ? 0f : idleRot;
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
	public void SwapCharacter(CharacterEntry entry)
	{
		if (entry?.ModelScene == null) return;

		EnsureInitialized();

		_activeEntry = entry;

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
		var newModel = entry.ModelScene.Instantiate<Node3D>();
		orientationFix.AddChild(newModel);
		
		// Update reference and re-initialize visuals
		_activeCharacter = orientationFix; 

		// Force an animation snap for the new model
		var animPlayer = newModel.GetNodeOrNull<AnimationPlayer>("AnimationPlayer") ?? 
						 newModel.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
		
		if (animPlayer != null)
		{
			// Set correct initial idle rotation immediately
			var root = animPlayer.GetNodeOrNull<Node3D>(animPlayer.RootNode);
			if (root != null)
			{
				root.RotationDegrees = new Vector3(root.RotationDegrees.X, entry.IdleRotation, root.RotationDegrees.Z);
			}

			if (animPlayer.HasAnimation("WalkingCycle_001"))
			{
				animPlayer.Play("WalkingCycle_001");
				animPlayer.Stop();
			}
		}

		// Apply camera offset
		_camera.Position = _baseCameraPos + entry.CameraOffset;

		// Force orientation update immediately
		UpdateAnimations(Vector3.Zero);
	}

	public void Sit(Chair chair)
	{
		if (_isSitting || _isTransitioning) return;

		EnsureInitialized();
		_isSitting = true;
		_isTransitioning = true;
		_currentChair = chair;
		_sittingYaw = 0f;
		_preSitPosition = GlobalPosition;

		if (_collisionShape != null)
			_collisionShape.Disabled = true;

		Vector3 targetPos = chair.GlobalPosition + (chair.GlobalTransform.Basis * (chair.SitOffset + (_activeEntry?.SittingOffset ?? Vector3.Zero)));
		Vector3 targetCamPos = _baseCameraPos + (_activeEntry?.CameraOffset ?? Vector3.Zero) + (_activeEntry?.SittingCameraOffset ?? Vector3.Zero);
		
		Tween transition = CreateTween();
		transition.SetParallel(true);
		
		// Body transition
		transition.TweenProperty(this, "global_position", targetPos, 0.6f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
		transition.TweenProperty(this, "global_rotation", chair.GlobalRotation, 0.6f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
		
		// Camera transition
		transition.TweenProperty(_camera, "position", targetCamPos, 0.6f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
		transition.TweenProperty(_camera, "fov", chair.SitFOV, 0.6f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);

		transition.Finished += () => {
			_isTransitioning = false;
			UpdateAnimations(Vector3.Zero);
		};

		UpdateAnimations(Vector3.Zero);
	}

	public void Unsit()
	{
		if (!_isSitting || _isTransitioning) return;

		_isSitting = false;
		_isTransitioning = true;
		_currentChair = null;

		Vector3 targetCamPos = _baseCameraPos + (_activeEntry?.CameraOffset ?? Vector3.Zero);
		
		Tween transition = CreateTween();
		transition.SetParallel(true);
		
		// Body transition back
		transition.TweenProperty(this, "global_position", _preSitPosition, 0.6f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
		// Note: body rotation usually stays as it was or we can snap it forward. 
		// Let's keep the rotation from the chair for a moment then allow mouse look to take over.
		
		// Camera transition back
		transition.TweenProperty(_camera, "position", targetCamPos, 0.6f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
		transition.TweenProperty(_camera, "rotation", new Vector3(_pitch, 0, 0), 0.6f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
		transition.TweenProperty(_camera, "fov", _baseFOV, 0.6f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);

		transition.Finished += () => {
			_isTransitioning = false;
			_sittingYaw = 0f;
			if (_collisionShape != null)
				_collisionShape.Disabled = false;
			UpdateAnimations(Vector3.Zero);
		};

		UpdateAnimations(Vector3.Zero);
	}
}
