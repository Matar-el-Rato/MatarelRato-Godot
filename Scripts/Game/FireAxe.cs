// ═══════════════════════════════════════════════════
// FireAxe.cs
// State machine:
//   Idle → Transitioning → InHand → Targeting → Striking → Disappearing
//
// Local player flow:
//   1. Interact          → axe reparented to camera, tweens to hand pose → Grabbed.
//      TableManager hands a list of axeable barrier squares to BeginTargeting().
//   2. While targeting   → axe follows mouse cursor over the tablero, hovering above
//                          the nearest valid barrier. Right-click / Escape cancels.
//   3. Click a barrier   → TargetSelected signal; TableManager sends use_fire_axe and
//                          calls StrikeBarrier(square, between-pieces world pos).
//   4. fire_axe_used     → BurnDisappear resets the prop.
//
// Remote flow:
//   fire_axe_used        → ApplyRemoteStrike(target world pos) descends a copy of the
//                          axe straight onto the barrier so every client sees the hit.
//
// The axe-follows-cursor view uses _Process to track the camera ray each frame, similar
// to how Handcuffs hovers the cuffs in the player's hand during targeting.
// ═══════════════════════════════════════════════════
using Godot;
using System.Collections.Generic;

public partial class FireAxe : Node3D
{
	[ExportGroup("Hand Positioning")]
	[Export] public Vector3 HandPosition   = new Vector3(0.18f, -0.18f, -0.40f);
	[Export] public Vector3 HandRotation   = new Vector3(0f, Mathf.Pi, Mathf.Pi / 2f);
	[Export] public float   TransitionTime = 0.5f;

	[ExportGroup("Targeting")]
	/// <summary>Height above the board at which the cursor-tracked axe hovers.</summary>
	[Export] public float CursorHoverHeight = 0.25f;
	/// <summary>Rotation applied to the axe while it follows the cursor (head down).</summary>
	[Export] public Vector3 CursorRotation = new Vector3(0f, 0f, -Mathf.Pi / 2f);

	[ExportGroup("Components")]
	[Export] public NodePath InteractablePath;

	// ── Signals ───────────────────────────────────────────────────────────────

	/// <summary>Fired when the grab tween finishes. TableManager calls BeginTargeting().</summary>
	[Signal] public delegate void GrabbedEventHandler();

	/// <summary>Fired when the player left-clicks a highlighted barrier square.</summary>
	[Signal] public delegate void BarrierSelectedEventHandler(int square);

	// ── State ─────────────────────────────────────────────────────────────────

	private enum FS { Idle, Transitioning, InHand, Targeting, Striking, Disappearing }
	private FS _state = FS.Idle;

	private Interactable _interactable;

	// Initial transform/parent captured at _Ready so a consumed axe resets back to the
	// hidden "fresh" pose PlayerItemSet expects for a future SpawnItem grant.
	private Node    _initialParent;
	private Vector3 _initialLocalPos;
	private Vector3 _initialLocalRot;
	private Vector3 _initialLocalScale = Vector3.One;

	// Stored on grab so ReturnToPlace can compute the correct world target.
	private Node    _originalParent;
	private Vector3 _originalPosition;
	private Vector3 _originalRotation;

	private Camera3D _grabCamera = null;

	// Targeting state.
	private readonly List<int> _validBarriers = new();
	private int _hoveredBarrier = -1;
	private TableroController _tablero;

	/// <summary>Last enabled state from TableManager — restored when ReturnToPlace fires.</summary>
	private bool _interactableEnabled = false;

	// When true, ResetToFresh calls FocusController.ExitFocus so the camera returns
	// to the sit position AFTER the full strike + burn animation, not immediately.
	private bool _exitFocusOnReset = false;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_interactable = GetNodeOrNull<Interactable>(InteractablePath);
		if (_interactable != null)
			_interactable.Interacted += OnInteracted;

		_initialParent     = GetParent();
		_initialLocalPos   = Position;
		_initialLocalRot   = Rotation;
		_initialLocalScale = Scale;
	}

	public override void _Process(double delta)
	{
		if (_grabCamera == null || !IsInstanceValid(_grabCamera)) return;

		if (_state == FS.InHand)
		{
			// Lock to the camera hand pose.
			GlobalTransform = new Transform3D(
				_grabCamera.GlobalBasis * Basis.FromEuler(HandRotation),
				_grabCamera.ToGlobal(HandPosition));
		}
		else if (_state == FS.Targeting)
		{
			TrackCursorOverTablero();
		}
	}

	// ── Public API ────────────────────────────────────────────────────────────

	public void SetInteractionEnabled(bool enabled)
	{
		_interactableEnabled = enabled;
		if (_interactable != null)
			_interactable.Enabled = enabled;
	}

	/// <summary>
	/// Enters targeting mode (player has focused the tablero while holding the axe).
	/// The axe detaches from the camera hand and starts tracking the cursor over the board.
	/// </summary>
	public void BeginTargeting(TableroController tablero, List<int> validBarrierSquares)
	{
		if (_state != FS.InHand) return;
		if (tablero == null || validBarrierSquares == null || validBarrierSquares.Count == 0)
			return; // caller is responsible for handling the empty case

		// Detach from the camera so world-space GlobalPosition writes in _Process don't
		// fight the camera transform every frame.
		if (_initialParent != null && IsInstanceValid(_initialParent) && GetParent() != _initialParent)
			Reparent(_initialParent, true);

		// Now that tablero focus is active, lock the Interactor so the player can't
		// accidentally click other interactables while aiming at the board.
		Interactor.IsLocked = true;

		_tablero = tablero;
		_validBarriers.Clear();
		_validBarriers.AddRange(validBarrierSquares);
		_hoveredBarrier = -1;

		_state = FS.Targeting;
	}

	/// <summary>
	/// Returns the axe from targeting back to the camera hand position (tablero focus exited).
	/// The player can re-focus the tablero to target again, or press Escape to cancel entirely.
	/// </summary>
	public void ReturnToHand()
	{
		if (_state != FS.Targeting) return;
		_tablero?.HideAxeBarrierHighlights();
		_hoveredBarrier = -1;
		Interactor.IsLocked = false; // player can interact with tablero again

		_state = FS.InHand;

		// Reparent back under the camera so the hand-lock in _Process takes over.
		if (_grabCamera != null && IsInstanceValid(_grabCamera) && GetParent() != _grabCamera)
			Reparent(_grabCamera, true);
	}

	/// <summary>Cancels entirely — returns the axe to its rest spot on the table.</summary>
	public void CancelTargeting()
	{
		if (_state != FS.Targeting && _state != FS.InHand) return;
		_tablero?.HideAxeBarrierHighlights();
		Interactor.IsLocked = false;
		ReturnToPlace();
	}

	/// <summary>
	/// Plays the striking animation: axe rapidly descends onto <paramref name="impactWorldPos"/>,
	/// fires the hitmarker sound, and stays embedded for a beat before BurnDisappear is called.
	/// </summary>
	public void StrikeBarrier(Vector3 impactWorldPos)
	{
		if (_state == FS.Disappearing) return;
		_state = FS.Striking;
		_grabCamera = null;  // stop _Process tracking
		_tablero?.HideAxeBarrierHighlights();

		// Camera will return to sit position AFTER the full animation (strike + burn).
		_exitFocusOnReset = true;

		// Detach from the camera (keep current world transform) so the strike tween isn't
		// fighting our _Process write each frame.
		if (GetParent() is Node3D && GetParent() != _initialParent)
			Reparent(_initialParent != null && IsInstanceValid(_initialParent)
				? _initialParent : (Node)GetTree().Root, true);

		// Head-down rotation for the impact, plus a small wind-up.
		var windupPos    = impactWorldPos + Vector3.Up * 0.55f;
		var impactRot    = (Basis.FromEuler(CursorRotation)).GetEuler();

		var tween = CreateTween();
		// Wind-up: lift slightly and rotate to head-down
		tween.SetParallel(true);
		tween.TweenProperty(this, "global_position", windupPos, 0.10f)
			.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
		tween.TweenProperty(this, "global_rotation", impactRot, 0.10f)
			.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);

		// Sequential strike — chain after the wind-up parallel tweens.
		tween.Chain();
		tween.TweenProperty(this, "global_position", impactWorldPos, 0.08f)
			.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
		tween.TweenCallback(Callable.From(PlayHitSound));
		tween.TweenCallback(Callable.From(SpawnImpactSparks));
		tween.TweenInterval(0.35f);
		tween.TweenCallback(Callable.From(BurnDisappear));
	}

	/// <summary>
	/// Remote-client view: descend an unattached copy of the axe straight down onto the
	/// target barrier so every player sees the strike. Resets to fresh afterwards.
	/// </summary>
	public void ApplyRemoteStrike(Vector3 impactWorldPos)
	{
		if (_state == FS.Disappearing) return;
		_state = FS.Striking;
		_grabCamera = null;

		// Detach from PlayerItemSet so we can move it freely.
		if (_initialParent != null && IsInstanceValid(_initialParent))
			Reparent(_initialParent.GetParent() ?? (Node)GetTree().Root, true);

		Visible = true;
		Scale   = _initialLocalScale;
		SetCollisionsEnabled(this, false);

		var startPos  = impactWorldPos + Vector3.Up * 1.2f;
		var impactRot = Basis.FromEuler(CursorRotation).GetEuler();
		GlobalPosition = startPos;
		GlobalRotation = impactRot;

		var tween = CreateTween();
		tween.TweenProperty(this, "global_position", impactWorldPos, 0.30f)
			.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
		tween.TweenCallback(Callable.From(PlayHitSound));
		tween.TweenCallback(Callable.From(SpawnImpactSparks));
		tween.TweenInterval(0.35f);
		tween.TweenCallback(Callable.From(BurnDisappear));
	}

	/// <summary>Resets the axe back to its hidden "fresh" state for future grants.</summary>
	public void BurnDisappear()
	{
		if (_state == FS.Disappearing) return;
		_state = FS.Disappearing;
		SetInteractionEnabled(false);
		SetCollisionsEnabled(this, false);

		var tween = CreateTween();
		tween.TweenProperty(this, "scale", new Vector3(0.001f, 0.001f, 0.001f), 0.35f)
			 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
		tween.Finished += ResetToFresh;
	}

	// ── Input ─────────────────────────────────────────────────────────────────

	public override void _Input(InputEvent @event)
	{
		switch (_state)
		{
			case FS.Targeting:
				if (@event is InputEventMouseButton mbT && mbT.Pressed)
				{
					if (mbT.ButtonIndex == MouseButton.Left && _hoveredBarrier >= 0)
					{
						GetViewport().SetInputAsHandled();
						int chosen = _hoveredBarrier;
						_state = FS.Striking;
						EmitSignal(SignalName.BarrierSelected, chosen);
					}
					else if (mbT.ButtonIndex == MouseButton.Right)
					{
						// Right-click exits tablero focus → FocusController fires
						// FocusStateChanged(false) → TableManager calls ReturnToHand().
						GetViewport().SetInputAsHandled();
						FocusController.Instance?.ExitFocus();
					}
				}
				// Escape is handled by FocusController itself (exits focus), which
				// triggers FocusStateChanged(false) → TableManager.ReturnToHand().
				break;

			case FS.InHand:
				// While in hand but NOT yet targeting, Escape/right-click cancels entirely.
				if (@event is InputEventMouseButton mbH && mbH.Pressed && mbH.ButtonIndex == MouseButton.Right)
				{
					GetViewport().SetInputAsHandled();
					CancelTargeting();
				}
				else if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
				{
					GetViewport().SetInputAsHandled();
					CancelTargeting();
				}
				break;
		}
	}

	private static bool IsCancelEvent(InputEvent e) =>
		e is InputEventKey key && key.Pressed && key.Keycode == Key.Escape;

	// ── Interaction ───────────────────────────────────────────────────────────

	private void OnInteracted()
	{
		if (_state != FS.Idle) return;
		_state = FS.Transitioning;

		_originalParent   = GetParent();
		_originalPosition = Position;
		_originalRotation = Rotation;

		if (_interactable != null) _interactable.Enabled = false;
		SetCollisionsEnabled(this, false);

		var camera = GetViewport().GetCamera3D();
		if (camera == null)
		{
			_state = FS.Idle;
			SetCollisionsEnabled(this, true);
			if (_interactable != null) _interactable.Enabled = _interactableEnabled;
			return;
		}

		_grabCamera = camera;
		Reparent(camera, true);

		var tweenIn = CreateTween().SetParallel(true);
		tweenIn.TweenProperty(this, "position", HandPosition, TransitionTime)
			.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
		tweenIn.TweenProperty(this, "rotation", HandRotation, TransitionTime)
			.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
		tweenIn.Finished += () =>
		{
			if (_state != FS.Transitioning) return;
			_state = FS.InHand;
			// Do NOT lock the Interactor here — the player needs to be able to click
			// the tablero's FocusInteractable to enter focus and start targeting.
			EmitSignal(SignalName.Grabbed);
		};
	}

	// ── Cursor tracking ───────────────────────────────────────────────────────

	private void TrackCursorOverTablero()
	{
		if (_tablero == null || !IsInstanceValid(_tablero)) return;
		if (_grabCamera == null || !IsInstanceValid(_grabCamera)) return;

		var mouse  = GetViewport().GetMousePosition();
		var origin = _grabCamera.ProjectRayOrigin(mouse);
		var dir    = _grabCamera.ProjectRayNormal(mouse);

		// Use the actual board surface Y from a known barrier square rather than the
		// tablero node's own origin Y, which is often different from where markers sit.
		float planeY = _validBarriers.Count > 0
			? _tablero.GetBoardWorldPosition(_validBarriers[0]).Y
			: _tablero.GlobalPosition.Y;

		Vector3 cursorWorld;
		if (Mathf.Abs(dir.Y) > 0.001f)
		{
			float tParam = (planeY - origin.Y) / dir.Y;
			if (tParam < 0.05f) tParam = 1.5f; // ray pointing away — fallback
			cursorWorld = origin + dir * tParam;
		}
		else
		{
			cursorWorld = origin + dir * 1.5f;
		}

		// Track nearest barrier for click detection.
		// The axe FOLLOWS THE CURSOR freely — red rings show valid targets.
		int   nearest      = -1;
		float nearestDist2 = float.MaxValue;
		foreach (int sq in _validBarriers)
		{
			Vector3 sqPos = _tablero.GetBoardWorldPosition(sq);
			float  dist2  = new Vector2(sqPos.X - cursorWorld.X, sqPos.Z - cursorWorld.Z).LengthSquared();
			if (dist2 < nearestDist2)
			{
				nearestDist2 = dist2;
				nearest      = sq;
			}
		}
		if (nearest != _hoveredBarrier)
		{
			_tablero.HideAxeBarrierHighlights();
			if (nearest >= 0)
				_tablero.ShowAxeBarrierHighlights(new List<int> { nearest });
			_hoveredBarrier = nearest;
		}

		// Position follows cursor, NOT the barrier — so it feels responsive.
		GlobalPosition = cursorWorld + Vector3.Up * CursorHoverHeight;
		GlobalRotation = Basis.FromEuler(CursorRotation).GetEuler();
	}

	// ── Return / reset ────────────────────────────────────────────────────────

	private void ReturnToPlace()
	{
		if (_state == FS.Idle || _state == FS.Disappearing) return;

		_grabCamera = null;
		_state = FS.Transitioning;

		var parent = _originalParent != null && IsInstanceValid(_originalParent)
			? _originalParent
			: (_initialParent != null && IsInstanceValid(_initialParent) ? _initialParent : null);
		if (parent == null)
		{
			ResetToFresh();
			return;
		}

		Reparent(parent, true);
		var targetWorldPos = parent is Node3D p ? p.ToGlobal(_originalPosition) : GlobalPosition;
		var targetWorldRot = parent is Node3D pp
			? (pp.GlobalBasis * Basis.FromEuler(_originalRotation)).GetEuler()
			: GlobalRotation;

		var tween = CreateTween().SetParallel(true);
		tween.TweenProperty(this, "global_position", targetWorldPos, TransitionTime)
			.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		tween.TweenProperty(this, "global_rotation", targetWorldRot, TransitionTime)
			.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		tween.Finished += () =>
		{
			Position = _originalPosition;
			Rotation = _originalRotation;
			_state = FS.Idle;
			SetCollisionsEnabled(this, true);
			Interactor.IsLocked = false;
			if (_interactable != null) _interactable.Enabled = _interactableEnabled;
		};
	}

	private void ResetToFresh()
	{
		if (!IsInsideTree()) return;

		// Exit tablero focus now that the full strike + burn animation has completed.
		if (_exitFocusOnReset)
		{
			_exitFocusOnReset = false;
			FocusController.Instance?.ExitFocus();
		}

		if (_initialParent != null && IsInstanceValid(_initialParent) && GetParent() != _initialParent)
			Reparent(_initialParent, true);

		Position = _initialLocalPos;
		Rotation = _initialLocalRot;
		Scale    = _initialLocalScale;

		Visible = false;
		SetCollisionsEnabled(this, false);
		if (_interactable != null) _interactable.Enabled = false;
		_interactableEnabled = false;

		_state              = FS.Idle;
		_hoveredBarrier     = -1;
		_validBarriers.Clear();
		_tablero            = null;
		_grabCamera         = null;
		Interactor.IsLocked = false;
	}

	// ── VFX / SFX ─────────────────────────────────────────────────────────────

	private void PlayHitSound()
	{
		var clip = GD.Load<AudioStream>("res://Assets/Sound FX/hitmarker.wav");
		if (clip == null) return;
		var sfx = new AudioStreamPlayer { Stream = clip, VolumeDb = 0f };
		GetTree().Root.AddChild(sfx);
		sfx.Play();
		sfx.Finished += () => sfx.QueueFree();
	}

	private void SpawnImpactSparks()
	{
		Vector3 pos = GlobalPosition;
		var particles = new CpuParticles3D { TopLevel = true };
		// Parent under the scene root so they survive the axe being scaled/reset.
		GetTree().Root.AddChild(particles);
		particles.GlobalPosition     = pos;
		particles.Amount             = 60;
		particles.Lifetime           = 0.55f;
		particles.OneShot            = true;
		particles.Explosiveness      = 0.95f;
		particles.EmissionShape      = CpuParticles3D.EmissionShapeEnum.Sphere;
		particles.EmissionSphereRadius = 0.03f;
		particles.Direction          = new Vector3(0f, 1f, 0f);
		particles.Spread             = 70f;
		particles.Gravity            = new Vector3(0f, -3.5f, 0f);
		particles.InitialVelocityMin = 0.8f;
		particles.InitialVelocityMax = 2.0f;
		particles.ScaleAmountMin     = 0.6f;
		particles.ScaleAmountMax     = 1.0f;

		var gradient = new Gradient();
		gradient.SetColor(0, new Color(1f, 1f, 0.7f, 1f));
		gradient.AddPoint(0.4f, new Color(1f, 0.55f, 0.1f, 0.9f));
		gradient.SetColor(gradient.GetPointCount() - 1, new Color(0.5f, 0.1f, 0f, 0f));
		particles.ColorRamp = gradient;

		particles.Mesh = new QuadMesh { Size = new Vector2(0.014f, 0.014f) };
		particles.MaterialOverride = new StandardMaterial3D
		{
			ShadingMode            = StandardMaterial3D.ShadingModeEnum.Unshaded,
			VertexColorUseAsAlbedo = true,
			BillboardMode          = StandardMaterial3D.BillboardModeEnum.Enabled,
			Transparency           = StandardMaterial3D.TransparencyEnum.Alpha,
		};
		particles.Emitting = true;
		GetTree().CreateTimer(particles.Lifetime + 0.4f).Timeout +=
			() => { if (IsInstanceValid(particles)) particles.QueueFree(); };
	}

	// ── Collision helper ──────────────────────────────────────────────────────

	private void SetCollisionsEnabled(Node node, bool enabled)
	{
		if (node is CollisionObject3D col)
		{
			col.InputRayPickable = enabled;
			if (node is PhysicsBody3D body)
				body.SetDeferred(Node.PropertyName.ProcessMode,
					(int)(enabled ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled));
		}
		if (node is CollisionShape3D shape)
			shape.SetDeferred(CollisionShape3D.PropertyName.Disabled, !enabled);

		foreach (Node child in node.GetChildren())
			SetCollisionsEnabled(child, enabled);
	}
}
