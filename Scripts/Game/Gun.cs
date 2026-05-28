// ═══════════════════════════════════════════════════
// Gun.cs
// Interactable gun prop: player picks it up (tweens to camera),
// shoots once on left-click (plays animation, audio, muzzle flash),
// then automatically returns to its original world position.
// ═══════════════════════════════════════════════════
using Godot;
using System;
using System.Collections.Generic;
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

	[ExportGroup("Initiative")]
	/// <summary>Optional click/misfire sound played by FireInitiativeShot when bang=false.</summary>
	[Export] public AudioStream MisfireClickSound;
	[Export] public float MisfireVolumeDb = 6f;

	// ── Signals ───────────────────────────────────────────────────────────────

	/// <summary>Fired when the grab tween finishes in gun_available mode.</summary>
	[Signal] public delegate void GrabbedEventHandler();

	/// <summary>Fired when the player clicks an opponent during gun targeting.</summary>
	[Signal] public delegate void TargetSelectedEventHandler(SeatToken token);

	/// <summary>Fired when the player cancels (right-click / Escape) while holding the gun.</summary>
	[Signal] public delegate void CancelledEventHandler();

	// ── Components ────────────────────────────────────────────────────────────

	private Node3D              _meshRoot;
	private AnimationPlayer     _animPlayer;
	private Interactable        _interactable;
	private Node3D              _muzzleFlash;
	private AudioStreamPlayer3D _audioPlayer;
	private AudioStreamPlayer3D _clickPlayer;

	private Vector3 _originalPosition;
	private Vector3 _originalRotation;
	private Node    _originalParent;

	private bool   _isInHand         = false;
	private double _timeSinceLastShot = 0;
	private bool   _hasBeenShot      = false;
	private bool   _isReturning      = false;
	private bool   _isTransitioning  = false;
	private bool   _isDisappearing   = false;

	// Initial transform/parent captured at _Ready so a consumed makarov resets back to the
	// hidden "fresh" pose PlayerItemSet expects for a future SpawnItem grant — even if
	// it was never grabbed on this client (remote-attacker view).
	private Node    _initialParent;
	private Vector3 _initialLocalPos;
	private Vector3 _initialLocalRot;
	private Vector3 _initialLocalScale = Vector3.One;

	// ── Gun-available targeting state ─────────────────────────────────────────

	/// <summary>True between gun_available and resolution (fire/skip).</summary>
	private bool _isAvailableMode     = false;
	/// <summary>True after BeginTargeting: player is aiming at an opponent.</summary>
	private bool _isTargeting         = false;
	/// <summary>Suppress auto-return after shooting; wait for BurnDisappear instead.</summary>
	private bool _waitForBurnDisappear = false;

	private readonly List<SeatToken> _targetTokens = new();
	private SeatToken _hoveredTarget;
	private Label3D   _exclamationLabel;

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

		if (MisfireClickSound != null)
		{
			_clickPlayer          = new AudioStreamPlayer3D();
			_clickPlayer.Stream   = MisfireClickSound;
			_clickPlayer.VolumeDb = MisfireVolumeDb;
			AddChild(_clickPlayer);
		}

		_initialParent     = GetParent();
		_initialLocalPos   = Position;
		_initialLocalRot   = Rotation;
		_initialLocalScale = Scale;
	}

	// ── Per-frame ─────────────────────────────────────────────────────────────

	public override void _Process(double delta)
	{
		// Targeting hover tracking while aiming at an opponent.
		if (_isInHand && _isTargeting)
		{
			UpdateGunTargetHover();
			return;
		}
		// Auto-return after shooting — suppressed in gun_available mode (BurnDisappear handles it).
		if (_isInHand && _hasBeenShot && !_isReturning && !_isTransitioning && !_waitForBurnDisappear)
		{
			_timeSinceLastShot += delta;
			if (_timeSinceLastShot >= ReturnDelay)
				ReturnToPlace();
		}
	}

	// ── Input ─────────────────────────────────────────────────────────────────

	public override void _Input(InputEvent @event)
	{
		if (!_isInHand || _isReturning || _isTransitioning) return;

		if (@event is InputEventMouseButton mb && mb.Pressed)
		{
			if (mb.ButtonIndex == MouseButton.Left)
			{
				if (_isTargeting)
				{
					var hit = RaycastForGunTarget();
					if (hit != null)
					{
						GetViewport().SetInputAsHandled();
						SelectGunTarget(hit);
					}
				}
				else if (!_isAvailableMode)
				{
					// Normal initiative / free-fire shot.
					Shoot();
				}
			}
			else if (mb.ButtonIndex == MouseButton.Right && _isAvailableMode)
			{
				GetViewport().SetInputAsHandled();
				CancelGunChoice();
			}
		}
		else if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.Escape && _isAvailableMode)
		{
			GetViewport().SetInputAsHandled();
			CancelGunChoice();
		}
	}

	// ── Interaction ───────────────────────────────────────────────────────────

	private void OnInteracted()
	{
		if (!_isAvailableMode) return;  // gun is only pickupable during the decision window
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
			_isInHand           = true;
			_isTransitioning    = false;
			_timeSinceLastShot  = 0;
			Interactor.IsLocked = true;
			if (_isAvailableMode)
				EmitSignal(SignalName.Grabbed);
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

	// ── Gun-available public API ──────────────────────────────────────────────

	/// <summary>
	/// Enables the gun for the "gun_available" mechanic: shows the "!" exclamation and
	/// makes the gun grabbable. Call with false to clean up if the turn ends early.
	/// </summary>
	public void SetAvailable(bool available)
	{
		_isAvailableMode = available;
		SetInteractionEnabled(available);
		if (available)
			SpawnExclamation();
		else
		{
			DespawnExclamation();
			DisableAllGunTargets();
			_isTargeting          = false;
			_waitForBurnDisappear = false;
		}
	}

	/// <summary>
	/// Called by TableManager after Grabbed fires. Highlights the given SeatTokens so
	/// the player can aim and left-click to shoot.
	/// </summary>
	public void BeginTargeting(List<SeatToken> targets)
	{
		if (!_isInHand) return;
		_isTargeting          = true;
		_waitForBurnDisappear = true;
		_targetTokens.Clear();
		if (targets != null) _targetTokens.AddRange(targets);
		foreach (var t in _targetTokens)
			if (IsInstanceValid(t)) t.SetTargetable(true);
	}

	/// <summary>Player right-clicked / pressed Escape — skip the shot, return gun to table.</summary>
	public void CancelGunChoice()
	{
		if (!_isAvailableMode) return;
		_isAvailableMode      = false;
		_isTargeting          = false;
		_waitForBurnDisappear = false;
		DisableAllGunTargets();
		DespawnExclamation();
		if (_isInHand && !_isReturning) ReturnToPlace();
		Interactor.IsLocked = false;
		EmitSignal(SignalName.Cancelled);
	}

	// ── Targeting helpers ─────────────────────────────────────────────────────

	private void UpdateGunTargetHover()
	{
		var hit = RaycastForGunTarget();
		if (hit == _hoveredTarget) return;
		if (IsInstanceValid(_hoveredTarget)) _hoveredTarget.ShowGunPrompt(false);
		_hoveredTarget = hit;
		if (IsInstanceValid(_hoveredTarget)) _hoveredTarget.ShowGunPrompt(true);
	}

	private SeatToken RaycastForGunTarget()
	{
		var camera = GetViewport().GetCamera3D();
		if (camera == null) return null;
		var mouse  = GetViewport().GetMousePosition();
		var origin = camera.ProjectRayOrigin(mouse);
		var end    = origin + camera.ProjectRayNormal(mouse) * 20f;
		var query  = PhysicsRayQueryParameters3D.Create(origin, end);
		query.CollideWithAreas  = false;
		query.CollideWithBodies = true;
		var result = GetWorld3D().DirectSpaceState.IntersectRay(query);
		if (result.Count == 0) return null;
		var collider = result["collider"].As<Node>();
		foreach (var token in _targetTokens)
			if (IsInstanceValid(token) && token.IsHitBy(collider)) return token;
		return null;
	}

	private void SelectGunTarget(SeatToken token)
	{
		DisableAllGunTargets();
		_isTargeting = false;
		DespawnExclamation();
		// Don't Shoot() here — server decides kill vs misfire.
		// OnGunResult will call FireInitiativeShot with the correct result.
		EmitSignal(SignalName.TargetSelected, token);
	}

	private void DisableAllGunTargets()
	{
		if (IsInstanceValid(_hoveredTarget)) _hoveredTarget.ShowGunPrompt(false);
		_hoveredTarget = null;
		foreach (var t in _targetTokens)
			if (IsInstanceValid(t)) t.SetTargetable(false);
		_targetTokens.Clear();
	}

	private void SpawnExclamation()
	{
		if (_exclamationLabel != null && IsInstanceValid(_exclamationLabel)) return;
		var font = ResourceLoader.Load<Font>("res://Assets/Fonts/Jersey10-Regular.ttf");
		_exclamationLabel = new Label3D
		{
			Text            = "!",
			Font            = font,
			FontSize        = 72,
			PixelSize       = 0.004f,
			Billboard       = BaseMaterial3D.BillboardModeEnum.Enabled,
			NoDepthTest     = true,
			Modulate        = new Color(1f, 0.88f, 0f),
			OutlineSize     = 12,
			OutlineModulate = new Color(0f, 0f, 0f, 1f),
			Position        = Vector3.Up * 0.15f,
		};
		AddChild(_exclamationLabel);
		var bob = _exclamationLabel.CreateTween().SetLoops();
		bob.TweenProperty(_exclamationLabel, "position:y", 0.22f, 0.5f)
		   .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		bob.TweenProperty(_exclamationLabel, "position:y", 0.12f, 0.5f)
		   .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
	}

	private void DespawnExclamation()
	{
		if (_exclamationLabel != null && IsInstanceValid(_exclamationLabel))
			_exclamationLabel.QueueFree();
		_exclamationLabel = null;
	}

	// ── Initiative API ────────────────────────────────────────────────────────

	public void SetInteractionEnabled(bool enabled)
	{
		if (_interactable != null)
			_interactable.Enabled = enabled;
	}

	// ── Initiative shot ───────────────────────────────────────────────────────

	/// <summary>
	/// Plays the trigger animation and, if bang, fires audio and muzzle flash.
	/// Used by the Ophanim initiative sequence — this gun is not in a player's hand.
	/// </summary>
	public void FireInitiativeShot(bool bang)
	{
		if (_animPlayer != null && _animPlayer.HasAnimation("Animation"))
		{
			_animPlayer.Stop();
			_animPlayer.SpeedScale = 2.0f;
			_animPlayer.Play("Animation");
		}

		if (bang)
		{
			_audioPlayer?.Stop();
			_audioPlayer?.Play();
			ShowMuzzleFlash();
		}
		else
		{
			_clickPlayer?.Play();
		}
	}

	// ── Consume ───────────────────────────────────────────────────────────────

	/// <summary>
	/// Scales the makarov out and resets it to its hidden "fresh" state so a future
	/// golden-square grant via PlayerItemSet.SpawnItem can re-spawn it. Crucially does NOT
	/// QueueFree — PlayerItemSet looks the node up by name ("Gun") in SpawnItem, so freeing
	/// it would make any subsequent grant fail with "not found in PlayerItemSet".
	/// </summary>
	public void BurnDisappear()
	{
		if (_isDisappearing) return;
		_isDisappearing = true;
		SetInteractionEnabled(false);
		SetCollisionsEnabled(this, false);

		var tween = CreateTween();
		tween.TweenProperty(this, "scale", new Vector3(0.001f, 0.001f, 0.001f), 0.5f)
			 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
		tween.Finished += ResetToFresh;
	}

	private void ResetToFresh()
	{
		if (!IsInsideTree()) return;

		// If the gun was reparented to a hand during use, return it under the PlayerItemSet
		// so SpawnItem's name lookup succeeds on the next grant.
		if (_initialParent != null && IsInstanceValid(_initialParent) && GetParent() != _initialParent)
			Reparent(_initialParent, true);

		Position = _initialLocalPos;
		Rotation = _initialLocalRot;
		Scale    = _initialLocalScale;

		Visible = false;
		if (_interactable != null) _interactable.Enabled = false;

		_isInHand             = false;
		_hasBeenShot          = false;
		_isReturning          = false;
		_isTransitioning      = false;
		_isDisappearing       = false;
		_timeSinceLastShot    = 0;
		_isAvailableMode      = false;
		_isTargeting          = false;
		_waitForBurnDisappear = false;
		DisableAllGunTargets();
		DespawnExclamation();
		Interactor.IsLocked = false;
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
