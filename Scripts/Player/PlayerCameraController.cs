// ═══════════════════════════════════════════════════
// PlayerCameraController.cs
// FPS CharacterBody3D: WASD movement, mouse look,
// jump, sprint, sit/unsit on chairs, footstep audio,
// and character model/animation management.
// ═══════════════════════════════════════════════════
using Godot;

/// <summary>
/// FPS-style controller for the player character.
///   - WASD to move horizontally; Space to jump; Shift to sprint.
///   - Mouse X rotates the body (yaw); Mouse Y tilts the camera (pitch).
///   - While sitting, yaw is applied to the camera only (body stays still)
///     and clamped to ±SITTING_YAW_LIMIT radians.
///   - <see cref="SwapCharacter"/> replaces the visible model at runtime.
///   - <see cref="Sit"/> / <see cref="Unsit"/> manage seated transitions.
///
/// Required scene tree:
///   CharacterBody3D (this script)
///   ├─ CollisionShape3D
///   ├─ character/  (model root)
///   └─ Camera3D
/// </summary>
public partial class PlayerCameraController : CharacterBody3D
{
	[Export] public float    WalkSpeed          = 5.0f;
	[Export] public float    SprintSpeed        = 10.0f;
	[Export] public float    JumpVelocity       = 6.0f;
	[Export] public float    MouseSensitivity   = 0.003f;   // radians per pixel
	[Export] public float    GravityMultiplier  = 3.0f;
	[Export] public NodePath CharacterModelPath = "character";
	/// <summary>When false, all movement input and physics are suppressed (e.g. during cutscenes or focus).</summary>
	[Export] public bool     MovementEnabled    = true;
	/// <summary>When false, mouse motion no longer rotates the camera (e.g. during NPC welcome sequences).</summary>
	public bool              MouseLookEnabled   = true;

	private Camera3D           _camera;
	private CollisionShape3D   _collisionShape;
	private float              _gravity;
	private float              _pitch = 0.0f;
	private Node3D             _activeCharacter;
	[Export] private CharacterEntry _activeEntry;
	private Vector3            _baseCameraPos;
	private float              _baseFOV;
	private Tween _cameraMoveTween; //for lerping between characters

	private bool  _isSitting      = false;
	private bool  _isTransitioning = false;
	public  bool  IsSitting => _isSitting;
	private Chair _currentChair;
	private float _sittingYaw     = 0f;
	private Vector3 _preSitPosition;

	// Maximum yaw rotation (in radians) the camera can swing while seated (~70°).
	private const float SITTING_YAW_LIMIT = 1.2f;

	private AudioStreamPlayer _footstepAudio;
	private float             _footstepTimer = 0f;
	private const float       WalkStepInterval = 0.54f;
	private const float       RunStepInterval  = 0.27f; // half period = twice as fast

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		EnsureInitialized();

		FloorSnapLength    = 0.3f;
		FloorConstantSpeed = true;
		FloorStopOnSlope   = true;
		ApplyFloorSnap();
	}

	/// <summary>
	/// Idempotent setup: resolves node references and project settings.
	/// Safe to call multiple times (e.g. from SwapCharacter).
	/// </summary>
	private void EnsureInitialized()
	{
		if (_camera == null)
		{
			_camera        = GetNode<Camera3D>("Camera3D");
			_baseCameraPos = _camera.Position;
			_baseFOV       = _camera.Fov;
		}
		if (_collisionShape == null)
			_collisionShape = GetNodeOrNull<CollisionShape3D>("CollisionShape3D");

		if (_activeCharacter == null)
			_activeCharacter = GetNodeOrNull<Node3D>(CharacterModelPath);

		_gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity");
		Input.MouseMode = Input.MouseModeEnum.Captured;

		if (_footstepAudio == null)
		{
			_footstepAudio        = new AudioStreamPlayer();
			_footstepAudio.Name   = "FootstepAudio";
			_footstepAudio.Stream = GD.Load<AudioStream>("res://Assets/Sound FX/walking_hard.wav");
			_footstepAudio.VolumeDb = -4f;
			// AddChild must be deferred — calling it inside another node's _Ready is blocked.
			CallDeferred(Node.MethodName.AddChild, _footstepAudio);
		}
	}

	// ── Mouse look ────────────────────────────────────────────────────────────

	public override void _UnhandledInput(InputEvent @event)
	{
		if (!MovementEnabled || _isTransitioning) return;

		if (@event is InputEventMouseMotion mm &&
			Input.MouseMode == Input.MouseModeEnum.Captured &&
			MouseLookEnabled)
		{
			if (_isSitting)
			{
				// Clamp yaw while seated — apply to camera only so the body stays still.
				_sittingYaw = Mathf.Clamp(
					_sittingYaw - mm.Relative.X * MouseSensitivity,
					-SITTING_YAW_LIMIT, SITTING_YAW_LIMIT);
			}
			else
			{
				// Standing: rotate the body so the character model follows the look direction.
				RotateY(-mm.Relative.X * MouseSensitivity);
			}

			// Pitch applies to the camera regardless of sitting state, clamped to ±89°.
			_pitch -= mm.Relative.Y * MouseSensitivity;
			_pitch  = Mathf.Clamp(_pitch, Mathf.DegToRad(-89f), Mathf.DegToRad(89f));

			_camera.Rotation = _isSitting
				? new Vector3(_pitch, _sittingYaw, 0)
				: new Vector3(_pitch, 0, 0);
		}

		// Toggle mouse capture with Escape.
		if (@event.IsActionPressed("ui_cancel"))
		{
			Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
				? Input.MouseModeEnum.Visible
				: Input.MouseModeEnum.Captured;
		}
	}

	// ── Physics ───────────────────────────────────────────────────────────────

	public override void _PhysicsProcess(double delta)
	{
		var vel = Velocity;

		// ── 1. Gravity ────────────────────────────────────────────────────────
		if (!IsOnFloor())
		{
			vel.Y -= _gravity * GravityMultiplier * (float)delta;
		}
		else
		{
			// Small negative Y keeps IsOnFloor() true without letting the player float.
			if (vel.Y <= 0)
				vel.Y = -0.1f;
		}

		// ── 2. Jump ───────────────────────────────────────────────────────────
		if (MovementEnabled && Input.IsActionPressed("jump") && IsOnFloor())
			vel.Y = JumpVelocity;

		// ── 3. Horizontal movement ────────────────────────────────────────────
		var direction = Vector3.Zero;

		if (MovementEnabled && !_isSitting)
		{
			if (Input.IsActionPressed("move_forward"))  direction -= Transform.Basis.Z;
			if (Input.IsActionPressed("move_backward")) direction += Transform.Basis.Z;
			if (Input.IsActionPressed("move_left"))     direction -= Transform.Basis.X;
			if (Input.IsActionPressed("move_right"))    direction += Transform.Basis.X;

			direction.Y = 0f;
			if (direction.LengthSquared() > 0f) direction = direction.Normalized();

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
			// Movement disabled or sitting: decelerate and handle unsit.
			vel.X = Mathf.MoveToward(vel.X, 0f, WalkSpeed);
			vel.Z = Mathf.MoveToward(vel.Z, 0f, WalkSpeed);

			if (_isSitting && (Input.IsActionJustPressed("sprint") || Input.IsKeyPressed(Key.Shift)))
				Unsit();

			// Force camera to the seated offset unless FocusController has taken over.
			bool isFocused = FocusController.Instance != null && FocusController.Instance.IsFocused;
			if (_isSitting && _activeEntry != null && !_isTransitioning && !isFocused)
				_camera.Position = _baseCameraPos + _activeEntry.CameraOffset + _activeEntry.SittingCameraOffset;
		}

		// ── 4. Apply velocity + collide ───────────────────────────────────────
		Velocity = vel;
		if (!_isSitting && !_isTransitioning)
			MoveAndSlide();
		else if (_isSitting && !_isTransitioning)
			GlobalPosition = GlobalPosition; // Locked in place while seated.

		// ── 5. Footsteps ──────────────────────────────────────────────────────
		UpdateFootsteps(direction, (float)delta);

		// ── 6. Animation ──────────────────────────────────────────────────────
		UpdateAnimations(direction);

		// ── 7. Fall recovery ──────────────────────────────────────────────────
		if (GlobalPosition.Y < -50f)
		{
			GlobalPosition = new Vector3(0f, 5f, 0f);
			Velocity       = Vector3.Zero;
		}
	}

	// ── Footsteps ─────────────────────────────────────────────────────────────

	private void UpdateFootsteps(Vector3 direction, float delta)
	{
		bool isMoving = direction.LengthSquared() > 0.001f && IsOnFloor() && !_isSitting;

		if (isMoving)
		{
			_footstepTimer -= delta;
			if (_footstepTimer <= 0f)
			{
				if (_footstepAudio != null && _footstepAudio.IsInsideTree())
					_footstepAudio.Play();
				_footstepTimer = Input.IsActionPressed("sprint") ? RunStepInterval : WalkStepInterval;
			}
		}
		else
		{
			// Reset so the next movement step plays immediately (no silent first frame).
			_footstepTimer = 0f;
		}
	}

	// ── Animations ────────────────────────────────────────────────────────────

	/// <summary>
	/// Selects and drives the correct animation (idle, walk, sit) for the active character.
	/// Also corrects the root bone's Y rotation to counteract Blender's 90° export offset.
	/// </summary>
	private void UpdateAnimations(Vector3 direction)
	{
		if (_activeCharacter == null) return;

		var animPlayer = _activeCharacter.GetNodeOrNull<AnimationPlayer>("AnimationPlayer")
					  ?? _activeCharacter.FindChild("AnimationPlayer", true, false) as AnimationPlayer;

		if (animPlayer == null) return;

		const string walkAnim = "WalkingCycle_001";
		const string sitAnim  = "sittingidle_001";

		if (_isSitting)
		{
			if (animPlayer.HasAnimation(sitAnim) && animPlayer.CurrentAnimation != sitAnim)
			{
				var anim = animPlayer.GetAnimation(sitAnim);
				if (anim != null) anim.LoopMode = Animation.LoopModeEnum.Linear;
				animPlayer.Play(sitAnim);
			}
			return; // Skip rotation correction while sitting.
		}

		// If we just stood up, stop the sit animation.
		if (animPlayer.CurrentAnimation == sitAnim)
		{
			animPlayer.Stop();
			if (animPlayer.HasAnimation(walkAnim))
			{
				animPlayer.Play(walkAnim);
				animPlayer.Stop(true); // Snap to frame 0 (standing T-pose).
			}
			GoToIdlePose(animPlayer);
		}

		if (direction.LengthSquared() > 0.001f)
		{
			if (animPlayer.HasAnimation(walkAnim))
			{
				if (animPlayer.CurrentAnimation != walkAnim)
				{
					var anim = animPlayer.GetAnimation(walkAnim);
					if (anim != null) anim.LoopMode = Animation.LoopModeEnum.Linear;
					animPlayer.Play(walkAnim);
				}
				// Reverse the cycle when walking backward; double speed when sprinting.
				float dot       = direction.Dot(-Transform.Basis.Z);
				float speedMult = Input.IsActionPressed("sprint") ? 2.0f : 1.0f;
				animPlayer.SpeedScale = (dot < -0.1f ? -1.0f : 1.0f) * speedMult;
			}
		}
		else
		{
			if (animPlayer.CurrentAnimation == walkAnim && animPlayer.IsPlaying())
			{
				animPlayer.SpeedScale = 1.0f;
				GoToIdlePose(animPlayer);
			}
		}

		// Force the root bone Y rotation so idle and walk both face the right direction.
		var root = animPlayer.GetNodeOrNull<Node3D>(animPlayer.RootNode);
		if (root != null)
		{
			float idleRot = _activeEntry?.IdleRotation ?? 0f;
			float targetY = (direction.LengthSquared() > 0.001f) ? 0f : idleRot;
			root.RotationDegrees = new Vector3(root.RotationDegrees.X, targetY, root.RotationDegrees.Z);
		}
	}

	/// <summary>
	/// Plays the RESET animation (or stops the player) to return the character to the
	/// bind-pose standing position. Fixes the 90° Y offset Blender bakes into exported models.
	/// Safe: applied exactly once per transition, not cumulative.
	/// </summary>
	private static void GoToIdlePose(AnimationPlayer animPlayer)
	{
		if (animPlayer.HasAnimation("RESET"))
			animPlayer.Play("RESET");
		else
			animPlayer.Stop(false);
	}

	// ── Character swapping ────────────────────────────────────────────────────

	/// <summary>
/// Replaces the visible character model and tweens the camera to the new offset.
/// </summary>
public void SwapCharacter(CharacterEntry entry, float duration = 0.8f)
{
	if (entry?.ModelScene == null) return;

	EnsureInitialized();
	_activeEntry = entry;

	var orientationFix = GetNodeOrNull<Node3D>("character/OrientationFix");
	if (orientationFix == null)
	{
		GD.PrintErr("PlayerCameraController: Could not find 'character/OrientationFix' to swap model.");
		return;
	}

	// --- 1. Model Swap Logic (Keep your existing logic) ---
	foreach (var child in orientationFix.GetChildren())
		child.QueueFree();

	var newModel = entry.ModelScene.Instantiate<Node3D>();
	orientationFix.AddChild(newModel);
	_activeCharacter = orientationFix;

	var animPlayer = newModel.GetNodeOrNull<AnimationPlayer>("AnimationPlayer")
					?? newModel.FindChild("AnimationPlayer", true, false) as AnimationPlayer;

	if (animPlayer != null)
	{
		var root = animPlayer.GetNodeOrNull<Node3D>(animPlayer.RootNode);
		if (root != null)
			root.RotationDegrees = new Vector3(root.RotationDegrees.X, entry.IdleRotation, root.RotationDegrees.Z);

		if (animPlayer.HasAnimation("WalkingCycle_001"))
		{
			animPlayer.Play("WalkingCycle_001");
			animPlayer.Stop();
		}
	}

	// --- 2. SMOOTH CAMERA TRANSITION ---
	Vector3 targetLocalPos = _baseCameraPos + entry.CameraOffset;

	// Kill any previous tween to prevent "fighting" if the user clicks fast
	if (_cameraMoveTween != null && _cameraMoveTween.IsValid())
		_cameraMoveTween.Kill();

	if (duration > 0)
	{
		_cameraMoveTween = CreateTween();
		_cameraMoveTween.TweenProperty(_camera, "position", targetLocalPos, duration)
			.SetTrans(Tween.TransitionType.Cubic)
			.SetEase(Tween.EaseType.Out);
	}
	else
	{
		_camera.Position = targetLocalPos;
	}

	UpdateAnimations(Vector3.Zero);
}

	// ── Sit / Unsit ───────────────────────────────────────────────────────────

	/// <summary>
	/// Transitions the player into the seated position on <paramref name="chair"/>.
	/// Disables the collision shape to avoid physics conflicts while sitting.
	/// </summary>
	public void Sit(Chair chair)
	{
		if (_isSitting || _isTransitioning) return;

		EnsureInitialized();
		_isSitting       = true;
		_isTransitioning = true;
		_currentChair    = chair;
		_sittingYaw      = 0f;
		_preSitPosition  = GlobalPosition;

		if (_collisionShape != null)
			_collisionShape.Disabled = true;

		Vector3 targetPos    = chair.GlobalPosition + (chair.GlobalTransform.Basis * (chair.SitOffset + (_activeEntry?.SittingOffset ?? Vector3.Zero)));
		Vector3 targetCamPos = _baseCameraPos + (_activeEntry?.CameraOffset ?? Vector3.Zero) + (_activeEntry?.SittingCameraOffset ?? Vector3.Zero);

		Tween transition = CreateTween();
		transition.SetParallel(true);

		transition.TweenProperty(this, "global_position", targetPos,           0.6f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
		transition.TweenProperty(this, "global_rotation", chair.GlobalRotation, 0.6f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
		transition.TweenProperty(_camera, "position", targetCamPos,            0.6f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
		transition.TweenProperty(_camera, "fov",      chair.SitFOV,            0.6f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);

		transition.Finished += () => {
			_isTransitioning = false;
			UpdateAnimations(Vector3.Zero);
		};

		UpdateAnimations(Vector3.Zero);
	}

	/// <summary>
	/// Returns the player to the standing position they occupied before sitting.
	/// Re-enables collision and restores the base FOV.
	/// </summary>
	public void Unsit()
	{
		if (!_isSitting || _isTransitioning) return;

		_isSitting       = false;
		_isTransitioning = true;
		_currentChair    = null;

		Vector3 targetCamPos = _baseCameraPos + (_activeEntry?.CameraOffset ?? Vector3.Zero);

		Tween transition = CreateTween();
		transition.SetParallel(true);

		transition.TweenProperty(this, "global_position", _preSitPosition,          0.6f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
		transition.TweenProperty(_camera, "position", targetCamPos,                 0.6f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
		transition.TweenProperty(_camera, "rotation", new Vector3(_pitch, 0, 0),    0.6f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
		transition.TweenProperty(_camera, "fov",      _baseFOV,                     0.6f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);

		transition.Finished += () => {
			_isTransitioning = false;
			_sittingYaw      = 0f;
			if (_collisionShape != null)
				_collisionShape.Disabled = false;
			UpdateAnimations(Vector3.Zero);
		};

		UpdateAnimations(Vector3.Zero);
	}
}
