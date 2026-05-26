// ═══════════════════════════════════════════════════
// FocusController.cs
// Singleton that smoothly moves the player camera to
// focus on a 3D prop (clipboard, board, etc.) and
// restores it when the player exits focus.
// ═══════════════════════════════════════════════════
using Godot;
using System;
using System.Diagnostics;

/// <summary>
/// Scene singleton that animates the player camera to look at a specific 3D target.
/// While focused:
/// - Player movement is disabled.
/// - <see cref="Interactor.IsLocked"/> is set to true.
/// - Mouse mode switches to Visible so the player can interact with viewport UIs.
/// Press Escape or call <see cref="ExitFocus"/> to return.
/// </summary>
public partial class FocusController : Node
{
	/// <summary>Global singleton — set in _Ready.</summary>
	public static FocusController Instance { get; private set; }

	[Export] public float    TransitionDuration = 0.3f;
	[Export] public NodePath PlayerPath         = "/root/Main/Player";
	[Export] public float    DefaultFOV         = 75.0f;

	private Camera3D               _playerCamera;
	private PlayerCameraController _playerBody;
	private bool                   _isFocused = false;
	private Transform3D            _originalCameraTransform;
	private float                  _originalFOV;
	private Node3D                 _currentFocusTarget;

	/// <summary>True while any target is being focused.</summary>
	public bool IsFocused => _isFocused;

	/// <summary>
	/// When true, new focus requests are silently dropped.
	/// Set by TableManager between roll_dice and dice_result to prevent focus spam.
	/// </summary>
	public static bool IsBlocked { get; set; } = false;

	/// <summary>Fired with true when focus starts, false when it fully ends.</summary>
	public static event Action<bool> FocusStateChanged;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		Instance = this;
		CallDeferred(MethodName.SetupReferences);
	}

	/// <summary>
	/// Resolves the player body and camera references.
	/// Falls back to a scene-wide search and then to the active viewport camera.
	/// </summary>
	private void SetupReferences()
	{
		Node3D player = GetNodeOrNull<Node3D>(PlayerPath);

		// Global scene search if the exported path fails.
		if (player == null)
			player = GetTree().Root.FindChild("Player", true, false) as Node3D;

		if (player != null)
		{
			_playerBody   = player as PlayerCameraController;
			_playerCamera = player.GetNodeOrNull<Camera3D>("Camera3D");

			// Wildcard fallback for unusual camera node names.
			if (_playerCamera == null)
				_playerCamera = player.FindChild("*Camera*", true, false) as Camera3D;
		}

		// Last resort: grab whatever camera is currently rendering the viewport.
		if (_playerCamera == null)
			_playerCamera = GetViewport().GetCamera3D();

		if (_playerCamera == null)
			GD.PrintErr("[FocusController] FAILED to find any camera!");
	}

	// ── Focus methods ─────────────────────────────────────────────────────────

	/// <summary>
	/// Moves the camera to an explicit world-space focal point transform.
	/// </summary>
	/// <param name="focalPoint">Node whose GlobalTransform becomes the camera's target.</param>
	/// <param name="target">The logical focus target tracked by <see cref="IsFocusedOn"/>.</param>
	public void FocusOn(Node3D focalPoint, Node3D target)
	{
		if (_playerCamera == null) SetupReferences();
		if (_isFocused || focalPoint == null || _playerCamera == null || IsBlocked) return;

		_isFocused              = true;
		_currentFocusTarget     = target;
		_originalCameraTransform = _playerCamera.GlobalTransform;

		if (_playerBody != null) _playerBody.MovementEnabled = false;
		Interactor.IsLocked      = true;
		Input.MouseMode          = Input.MouseModeEnum.Visible;

		TweenCamera(focalPoint.GlobalTransform, DefaultFOV);
		FocusStateChanged?.Invoke(true);
	}

	/// <summary>
	/// Modular focus: calculates a camera position in front of the target surface
	/// using its Y-normal as the "facing" direction.
	/// </summary>
	/// <param name="target">The Node3D to focus on (e.g. the clipboard Node3D).</param>
	/// <param name="distance">How far in front of the target's surface the camera sits.</param>
	/// <param name="positionOffset">Additional offset in the target's local space.</param>
	/// <param name="rotationOffset">Roll/up-vector rotation around the normal (degrees).</param>
	/// <param name="targetFOV">Camera field-of-view while focused.</param>
	public void FocusOnModular(Node3D target, float distance = 0.4f, Vector3 positionOffset = default, float rotationOffset = 0f, float targetFOV = 75.0f)
	{
		if (_playerCamera == null) SetupReferences();
		if (_isFocused || target == null || _playerCamera == null || IsBlocked) return;

		_isFocused               = true;
		_currentFocusTarget      = target;
		_originalCameraTransform = _playerCamera.GlobalTransform;
		_originalFOV             = _playerCamera.Fov;

		if (_playerBody != null)
		{
			_playerBody.MovementEnabled = false;
			_playerBody.Velocity        = Vector3.Zero;
		}

		// Decouple camera from the player body hierarchy so transforms don't fight.
		// Preserve the camera's current global position when switching to TopLevel.
		Vector3 currentGlobalPos = _playerCamera.GlobalPosition;
		_playerCamera.TopLevel = true;
		_playerCamera.GlobalPosition = currentGlobalPos;

		Interactor.IsLocked = true;
		Input.MouseMode     = Input.MouseModeEnum.Visible;

		Transform3D targetTransform = target.GlobalTransform;
		Vector3 normal = targetTransform.Basis.Y.Normalized();

		// Position: stand in front of the surface at the requested distance.
		Vector3 focusPos = target.GlobalPosition + (normal * distance);
		focusPos += targetTransform.Basis.X * positionOffset.X;
		focusPos += targetTransform.Basis.Y * positionOffset.Y;
		focusPos += targetTransform.Basis.Z * positionOffset.Z;

		// Camera looks toward the surface.
		Vector3 forward = -normal;

		// Orient so the player always appears on the LEFT of the screen.
		// Project the board→player vector onto the surface plane, then rotate it 90°
		// around the normal so the camera's right axis points away from the player.
		Vector3 upVector;
		if (_playerBody != null)
		{
			Vector3 toPlayer        = _playerBody.GlobalPosition - target.GlobalPosition;
			Vector3 toPlayerInPlane = (toPlayer - toPlayer.Dot(normal) * normal).Normalized();
			upVector = toPlayerInPlane.Cross(normal);
		}
		else
		{
			upVector = targetTransform.Basis.Z.Normalized();
		}

		if (rotationOffset != 0f)
			upVector = upVector.Rotated(normal, Mathf.DegToRad(rotationOffset));

		try
		{
			Transform3D focusTransform = new Transform3D();
			focusTransform.Basis  = Basis.LookingAt(forward, upVector);
			focusTransform.Origin = focusPos;
			TweenCamera(focusTransform, targetFOV);
		}
		catch (Exception e)
		{
			// LookingAt can throw if forward and up are parallel — use a manual fallback.
			GD.PrintErr($"[FocusController] Basis error: {e.Message}. falling back to manual basis.");
			Transform3D focusTransform = new Transform3D();
			focusTransform.Basis.Z = normal;
			focusTransform.Basis.Y = upVector;
			focusTransform.Basis.X = focusTransform.Basis.Y.Cross(focusTransform.Basis.Z).Normalized();
			focusTransform.Origin  = focusPos;
			TweenCamera(focusTransform, targetFOV);
		}
		FocusStateChanged?.Invoke(true);
	}

	/// <summary>
	/// Smoothly restores the camera to its pre-focus transform and re-enables player control.
	/// </summary>
	public void ExitFocus()
	{
		if (!_isFocused || _playerCamera == null) return;

		Tween tween = CreateTween();
		tween.SetParallel(true);
		tween.TweenProperty(_playerCamera, "global_transform", _originalCameraTransform, TransitionDuration)
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.InOut);
		tween.TweenProperty(_playerCamera, "fov", _originalFOV, TransitionDuration)
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.InOut);

		tween.Finished += () => {
			_isFocused = false;
			// Re-attach camera to the player body hierarchy.
			_playerCamera.TopLevel = false;

			if (_playerBody != null) _playerBody.MovementEnabled = true;
			Interactor.IsLocked  = false;
			Input.MouseMode      = Input.MouseModeEnum.Captured;
			_currentFocusTarget  = null;
			FocusStateChanged?.Invoke(false);
		};
	}

	/// <summary>Returns true when currently focused and the active target is <paramref name="target"/>.</summary>
	public bool IsFocusedOn(Node3D target)
	{
		return _isFocused && _currentFocusTarget == target;
	}

	// ── Input ─────────────────────────────────────────────────────────────────

	public override void _Input(InputEvent @event)
	{
		// Focus is exited only through the on-screen ✕ button.
	}

	// ── Private helpers ───────────────────────────────────────────────────────

	private void TweenCamera(Transform3D targetTransform, float targetFOV)
	{
		Tween tween = CreateTween();
		tween.SetParallel(true);
		tween.TweenProperty(_playerCamera, "global_transform", targetTransform, TransitionDuration)
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.InOut);
		tween.TweenProperty(_playerCamera, "fov", targetFOV, TransitionDuration)
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.InOut);
	}
}
