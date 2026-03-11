using Godot;
using System;
using System.Diagnostics;

public partial class FocusController : Node
{
	public static FocusController Instance { get; private set; }

	[Export] public float TransitionDuration = 0.6f;
	[Export] public NodePath PlayerPath = "/root/Main/Player";
	[Export] public float DefaultFOV = 75.0f;

	private Camera3D _playerCamera;
	private PlayerCameraController _playerBody;
	private bool _isFocused = false;
	public bool IsFocused => _isFocused;
	private Transform3D _originalCameraTransform;
	private Node3D _currentFocusTarget;

	public override void _Ready()
	{
		Instance = this;
		GD.Print("[FocusController] Instance set in _Ready");
		CallDeferred(MethodName.SetupReferences);
	}

	private void SetupReferences()
	{
		GD.Print($"[FocusController] SetupReferences starting. PlayerPath={PlayerPath}");
		
		Node3D player = GetNodeOrNull<Node3D>(PlayerPath);
		
		// Fallback: search globally if the path fails
		if (player == null)
		{
			player = GetTree().Root.FindChild("Player", true, false) as Node3D;
			if (player != null) GD.Print("[FocusController] Player found via global search");
		}
		else
		{
			GD.Print("[FocusController] Player found via path");
		}

		if (player != null)
		{
			_playerBody = player as PlayerCameraController;
			_playerCamera = player.GetNodeOrNull<Camera3D>("Camera3D");
			
			// Fallback: search for any camera in player
			if (_playerCamera == null)
			{
				_playerCamera = player.FindChild("*Camera*", true, false) as Camera3D;
			}
		}

		// Absolute fallback: current viewport camera
		if (_playerCamera == null)
		{
			_playerCamera = GetViewport().GetCamera3D();
			if (_playerCamera != null) GD.Print("[FocusController] Camera found via GetCamera3D fallback");
		}

		if (_playerCamera == null)
		{
			GD.PrintErr("[FocusController] FAILED to find any camera!");
		}
		else
		{
			GD.Print($"[FocusController] SetupReferences complete. Camera={_playerCamera.Name}");
		}
	}

	public void FocusOn(Node3D focalPoint, Node3D target)
	{
		if (_playerCamera == null) SetupReferences();

		GD.Print($"[FocusController] FocusOn called. Focused={_isFocused}, FocalPoint={focalPoint?.Name}, Target={target?.Name}");
		if (_isFocused || focalPoint == null || _playerCamera == null) 
		{
			GD.PrintErr($"[FocusController] Focus rejected: isFocused={_isFocused}, focalPoint={focalPoint == null}, playerCamera={_playerCamera == null}");
			return;
		}

		_isFocused = true;
		_currentFocusTarget = target;
		_originalCameraTransform = _playerCamera.GlobalTransform;

		// Lock player movement and interaction
		if (_playerBody != null) _playerBody.MovementEnabled = false;
		Interactor.IsLocked = true;
		Input.MouseMode = Input.MouseModeEnum.Visible;

		// Use the focal point's global transform for the camera transition
		TweenCamera(focalPoint.GlobalTransform, DefaultFOV);
	}

	/// <summary>
	/// Modular Focus: Focuses on a target by calculating a focal point based on its orientation and a distance.
	/// </summary>
	public void FocusOnModular(Node3D target, float distance = 0.4f, Vector3 positionOffset = default, float rotationOffset = 0f, float targetFOV = 75.0f)
	{
		if (_playerCamera == null) SetupReferences();
		if (_isFocused || target == null || _playerCamera == null) return;

		GD.Print($"[FocusController] FocusOnModular: target={target.Name}, distance={distance}, posOffset={positionOffset}, rotOffset={rotationOffset}, fov={targetFOV}");
		GD.Print($"[FocusController] Target Basis - X:{target.GlobalTransform.Basis.X}, Y:{target.GlobalTransform.Basis.Y}, Z:{target.GlobalTransform.Basis.Z}");
		GD.Print($"[FocusController] Caller: {new StackTrace().GetFrame(1).GetMethod().Name} in {new StackTrace().GetFrame(1).GetMethod().DeclaringType.Name}");

		_isFocused = true;
		_currentFocusTarget = target;
		_originalCameraTransform = _playerCamera.GlobalTransform;

		if (_playerBody != null) 
		{
			_playerBody.MovementEnabled = false;
			_playerBody.Velocity = Vector3.Zero;
		}
		
		// Decouple camera from player body hierarchy to prevent fighting transforms
		_playerCamera.TopLevel = true;
		
		Interactor.IsLocked = true;
		Input.MouseMode = Input.MouseModeEnum.Visible;

		Transform3D targetTransform = target.GlobalTransform;
		Vector3 normal = targetTransform.Basis.Y.Normalized();
		
		// Position: target center + normal * distance
		Vector3 focusPos = target.GlobalPosition + (normal * distance);
		
		// Apply local position offset
		focusPos += targetTransform.Basis.X * positionOffset.X;
		focusPos += targetTransform.Basis.Y * positionOffset.Y;
		focusPos += targetTransform.Basis.Z * positionOffset.Z;
		
		GD.Print($"[FocusController] target={target.Name}, dist={distance}, finalPos={focusPos}, targetPos={target.GlobalPosition}");
		
		// Rotation calculation using LookingAt for stability
		Vector3 forward = -normal; // Camera looks towards the surface
		Vector3 upVector = targetTransform.Basis.Z.Normalized(); // Default "up" is target Z
		
		if (rotationOffset != 0f)
		{
			upVector = upVector.Rotated(normal, Mathf.DegToRad(rotationOffset));
		}
		
		try 
		{
			Transform3D focusTransform = new Transform3D();
			focusTransform.Basis = Basis.LookingAt(forward, upVector);
			focusTransform.Origin = focusPos;
			TweenCamera(focusTransform, targetFOV);
		}
		catch (Exception e)
		{
			GD.PrintErr($"[FocusController] Basis error: {e.Message}. falling back to manual basis.");
			// Manual fallback if LookingAt fails (e.g. parallel vectors)
			Transform3D focusTransform = new Transform3D();
			focusTransform.Basis.Z = normal; 
			focusTransform.Basis.Y = upVector;
			focusTransform.Basis.X = focusTransform.Basis.Y.Cross(focusTransform.Basis.Z).Normalized();
			focusTransform.Origin = focusPos;
			TweenCamera(focusTransform, targetFOV);
		}
	}

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

	public void ExitFocus()
	{
		if (!_isFocused || _playerCamera == null) return;

		Tween tween = CreateTween();
		tween.SetParallel(true);
		tween.TweenProperty(_playerCamera, "global_transform", _originalCameraTransform, TransitionDuration)
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.InOut);
			
		tween.TweenProperty(_playerCamera, "fov", DefaultFOV, TransitionDuration)
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.InOut);

		tween.Finished += () => {
			_isFocused = false;
			// Re-anchor camera to player body
			_playerCamera.TopLevel = false;
			
			if (_playerBody != null) _playerBody.MovementEnabled = true;
			Interactor.IsLocked = false;
			Input.MouseMode = Input.MouseModeEnum.Captured;
			_currentFocusTarget = null;
		};
	}

	public bool IsFocusedOn(Node3D target)
	{
		return _isFocused && _currentFocusTarget == target;
	}

	public override void _Input(InputEvent @event)
	{
		if (_isFocused && @event.IsActionPressed("ui_cancel"))
		{
			ExitFocus();
		}
	}
}
