// ═══════════════════════════════════════════════════
// Gun.cs
// Interactable gun prop: player picks it up (tweens to camera),
// shoots once on left-click (plays animation, audio, muzzle flash),
// then automatically returns to its original world position.
// ═══════════════════════════════════════════════════
using Godot;
using System;
using System.Threading.Tasks;

/// <summary>
/// Manages the gun prop lifecycle:
/// 1. Player interacts → gun reparented to the camera and tweened to <see cref="HandPosition"/>.
/// 2. Left-click → fires the "Animation" clip, audio, and muzzle flash.
/// 3. After <see cref="ReturnDelay"/> seconds → gun reparented back to its original parent
///    and tweened to its original transform.
/// <see cref="Interactor.IsLocked"/> is held true while the gun is in hand to
/// prevent other interactions from firing.
/// </summary>
public partial class Gun : Node3D
{
	[ExportGroup("Hand Positioning")]
	/// <summary>Local position offset relative to the camera when held.</summary>
	[Export] public Vector3 HandPosition   = new Vector3(0.15f, -0.1f, -0.3f);
	/// <summary>Local rotation (radians) when held.</summary>
	[Export] public Vector3 HandRotation   = new Vector3(0, Mathf.Pi, 0);
	[Export] public float   TransitionTime = 0.5f;
	/// <summary>Seconds after shooting before the gun returns to the world.</summary>
	[Export] public double  ReturnDelay    = 1.0;

	[ExportGroup("Components")]
	[Export] public NodePath InteractablePath;
	[Export] public NodePath MuzzleFlashPath;
	[Export] public NodePath AudioPlayerPath;
	[Export] public NodePath MeshRootPath = "makarov";

	private Node3D              _meshRoot;
	private AnimationPlayer     _animPlayer;
	private Interactable        _interactable;
	private Node3D              _muzzleFlash;
	private AudioStreamPlayer3D _audioPlayer;

	private Vector3 _originalPosition;
	private Vector3 _originalRotation;
	private Node    _originalParent;

	private bool   _isInHand       = false;
	private double _timeSinceLastShot = 0;
	private bool   _hasBeenShot    = false;
	private bool   _isReturning    = false;
	private bool   _isTransitioning = false;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_meshRoot = GetNodeOrNull<Node3D>(MeshRootPath);
		if (_meshRoot != null)
		{
			_animPlayer = _meshRoot.GetNodeOrNull<AnimationPlayer>("AnimationPlayer")
						?? _meshRoot.FindChild("AnimationPlayer", true, false) as AnimationPlayer;

			if (_animPlayer == null)
				GD.PrintErr($"Gun: AnimationPlayer NOT found under '{MeshRootPath}'!");
		}
		else
		{
			GD.PrintErr($"Gun: MeshRoot '{MeshRootPath}' NOT found!");
		}

		_interactable = GetNodeOrNull<Interactable>(InteractablePath);
		_muzzleFlash  = GetNodeOrNull<Node3D>(MuzzleFlashPath);
		_audioPlayer  = GetNodeOrNull<AudioStreamPlayer3D>(AudioPlayerPath);

		if (_interactable != null)
			_interactable.Interacted += OnInteracted;
	}

	// ── Per-frame ─────────────────────────────────────────────────────────────

	public override void _Process(double delta)
	{
		// Start the return tween once ReturnDelay has elapsed after shooting.
		if (_isInHand && _hasBeenShot && !_isReturning && !_isTransitioning)
		{
			_timeSinceLastShot += delta;
			if (_timeSinceLastShot >= ReturnDelay)
				ReturnToPlace();
		}
	}

	// ── Input ─────────────────────────────────────────────────────────────────

	public override void _Input(InputEvent @event)
	{
		if (_isInHand && !_isReturning && !_isTransitioning)
		{
			if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
				Shoot();
		}
	}

	// ── Interaction ───────────────────────────────────────────────────────────

	private void OnInteracted()
	{
		if (_isInHand || _isTransitioning || _isReturning) return;

		_isTransitioning = true;
		_hasBeenShot     = false;

		_originalParent   = GetParent();
		_originalPosition = Position;
		_originalRotation = Rotation;

		// Disable physics collisions to prevent the gun from clipping into the player.
		SetCollisionsEnabled(this, false);

		var camera = GetViewport().GetCamera3D();
		if (camera != null)
			ReparentToHand(camera);
	}

	// ── Pick-up / Return ──────────────────────────────────────────────────────

	/// <summary>
	/// Reparents the gun to the camera and tweens it to the hand offset.
	/// </summary>
	private void ReparentToHand(Node3D handParent)
	{
		Reparent(handParent, true);

		var tween = CreateTween();
		tween.SetParallel(true);
		tween.TweenProperty(this, "position", HandPosition, TransitionTime).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
		tween.TweenProperty(this, "rotation", HandRotation, TransitionTime).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);

		tween.Finished += () =>
		{
			_isInHand          = true;
			_isTransitioning   = false;
			_timeSinceLastShot = 0;
			Interactor.IsLocked = true;
		};
	}

	/// <summary>
	/// Reparents the gun back to its original world parent and tweens it home.
	/// Re-enables collisions and unlocks the Interactor when done.
	/// </summary>
	private void ReturnToPlace()
	{
		_isReturning = true;
		_isInHand    = false;

		Reparent(_originalParent, true);

		var tween = CreateTween();
		tween.SetParallel(true);
		tween.TweenProperty(this, "position", _originalPosition, TransitionTime).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		tween.TweenProperty(this, "rotation", _originalRotation, TransitionTime).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);

		tween.Finished += () =>
		{
			_isReturning = false;
			SetCollisionsEnabled(this, true);
			Interactor.IsLocked = false;
		};
	}

	// ── Shoot ─────────────────────────────────────────────────────────────────

	private void Shoot()
	{
		_timeSinceLastShot = 0;
		_hasBeenShot       = true;

		if (_animPlayer != null)
		{
			if (_animPlayer.HasAnimation("Animation"))
			{
				_animPlayer.Stop();
				_animPlayer.SpeedScale = 2.0f;
				_animPlayer.Play("Animation");
			}
			else
			{
				GD.PrintErr("Gun: Animation 'Animation' not found in AnimationPlayer!");
			}
		}

		_audioPlayer?.Stop();
		_audioPlayer?.Play();

		ShowMuzzleFlash();
	}

	/// <summary>
	/// Triggers the BinbunVFX muzzle flash node via its "play" method (GDScript interop).
	/// </summary>
	private void ShowMuzzleFlash()
	{
		if (_muzzleFlash == null) return;
		if (_muzzleFlash.HasMethod("play"))
			_muzzleFlash.Call("play");
	}

	// ── Collision helper ──────────────────────────────────────────────────────

	/// <summary>
	/// Recursively enables or disables all physics collision objects in the subtree.
	/// Used to prevent the gun from clipping or triggering physics while held.
	/// </summary>
	private void SetCollisionsEnabled(Node node, bool enabled)
	{
		if (node is CollisionObject3D collisionObject)
		{
			collisionObject.InputRayPickable = enabled;
			if (node is PhysicsBody3D body)
			{
				// Use SetDeferred because physics state can't change mid-physics-step.
				body.SetDeferred(Node.PropertyName.ProcessMode,
					(int)(enabled ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled));
			}
		}

		if (node is CollisionShape3D shape)
			shape.SetDeferred(CollisionShape3D.PropertyName.Disabled, !enabled);

		foreach (Node child in node.GetChildren())
			SetCollisionsEnabled(child, enabled);
	}
}
