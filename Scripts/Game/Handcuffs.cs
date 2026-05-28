// ═══════════════════════════════════════════════════
// Handcuffs.cs
// State machine:
//   Idle → Transitioning → InHand → Targeting → Flying → Placed → Disappearing
//
// Local player flow:
//   1. Interact  → model TopLevel=true, tween to camera hand → Grabbed signal
//   2. Click SeatToken → PlayerSelected signal → PlaceOnPlayerHead → fly → hover
//   3. handcuff_skip arrives → BurnDisappear → fire VFX + remove
//
// Remote flow:
//   handcuffs_applied → ApplyRemoteToHead → same fly + hover
//
// CRASH FIX: Interactable.AutoGenerateCollision=false on this scene.
// A plain PickupBody capsule handles raycasts.  _handcuffModel (purely visual,
// no StaticBody3D children) is the only node that ever moves, using TopLevel=true
// so its world transform is independent of the stationary root.  The root node
// (which previously had trimesh StaticBody3D descendants) never moves.
// ═══════════════════════════════════════════════════
using Godot;
using System.Collections.Generic;

public partial class Handcuffs : Node3D
{
	[ExportGroup("Hand Positioning")]
	[Export] public Vector3 HandPosition   = new Vector3(0.1f, -0.08f, -0.25f);
	[Export] public Vector3 HandRotation   = new Vector3(0f, Mathf.Pi / 2f, 0f);
	[Export] public float   TransitionTime = 0.5f;

	/// <summary>World-space rotation the cuffs settle into once placed on a head (90° yaw by default).</summary>
	[Export] public Vector3 PlacedRotation = new Vector3(0f, Mathf.Pi / 2f, 0f);

	[ExportGroup("Components")]
	[Export] public NodePath InteractablePath;
	/// <summary>Path to the visual-only model node (no physics children). Only this node moves.</summary>
	[Export] public NodePath HandcuffModelPath;

	// ── Signals ───────────────────────────────────────────────────────────────

	/// <summary>Fired when the grab tween finishes. TableManager calls BeginTargeting() after this.</summary>
	[Signal] public delegate void GrabbedEventHandler();

	/// <summary>Fired when the player clicks an opponent during targeting.</summary>
	[Signal] public delegate void PlayerSelectedEventHandler(SeatToken token);

	// ── State ─────────────────────────────────────────────────────────────────

	private enum HS { Idle, Transitioning, InHand, Targeting, Flying, Placed, Disappearing }
	private HS _state = HS.Idle;

	private Interactable             _interactable;
	private Node3D                   _handcuffModel;
	private Vector3                  _modelOriginalLocalPos;
	private Vector3                  _modelOriginalLocalRot;
	private Vector3                  _modelOriginalLocalScale;

	// Stored so ReturnToPlace can compute the correct world target (root never moves).
	private Vector3                  _originalPosition;
	private Vector3                  _originalRotation;

	private readonly List<SeatToken> _targetTokens = new();
	private SeatToken                _hoveredTarget;
	private Vector3                  _placedBasePos;
	private Tween                    _hoverTween;

	/// <summary>Camera used while InHand/Targeting. Null otherwise.</summary>
	private Camera3D _grabCamera = null;

	/// <summary>
	/// The enabled state last set by TableManager (turn gate).
	/// ReturnToPlace restores to this so player-initiated cancel re-enables interaction
	/// while a turn-end cancel keeps it disabled.
	/// </summary>
	private bool _interactableEnabled = false;

	private static readonly Color _burnColor = new Color(1.0f, 0.5f, 0.2f);

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_interactable = GetNodeOrNull<Interactable>(InteractablePath);
		_handcuffModel = HandcuffModelPath != null && !HandcuffModelPath.IsEmpty
			? GetNodeOrNull<Node3D>(HandcuffModelPath)
			: null;

		if (_handcuffModel != null)
		{
			_modelOriginalLocalPos   = _handcuffModel.Position;
			_modelOriginalLocalRot   = _handcuffModel.Rotation;
			_modelOriginalLocalScale = _handcuffModel.Scale;
		}

		if (_interactable != null)
			_interactable.Interacted += OnInteracted;
	}

	// ── Process ───────────────────────────────────────────────────────────────

	public override void _Process(double delta)
	{
		// While InHand or Targeting, lock the visual model to the camera hand position.
		// The root node stays stationary; only _handcuffModel (no physics children) moves.
		if (_grabCamera == null || !IsInstanceValid(_grabCamera)) return;
		if (_state != HS.InHand && _state != HS.Targeting) return;
		if (_handcuffModel == null) return;

		_handcuffModel.GlobalTransform = new Transform3D(
			_grabCamera.GlobalBasis * Basis.FromEuler(HandRotation),
			_grabCamera.ToGlobal(HandPosition));

		if (_state == HS.Targeting)
			UpdateTargetHover();
	}

	/// <summary>While targeting, show the "Handcuff {name}" prompt on the aimed-at player only.</summary>
	private void UpdateTargetHover()
	{
		var hit = RaycastForTarget();
		if (hit == _hoveredTarget) return;

		if (IsInstanceValid(_hoveredTarget))
			_hoveredTarget.ShowHandcuffPrompt(false);
		_hoveredTarget = hit;
		if (IsInstanceValid(_hoveredTarget))
			_hoveredTarget.ShowHandcuffPrompt(true);
	}

	// ── Input ─────────────────────────────────────────────────────────────────

	public override void _Input(InputEvent @event)
	{
		switch (_state)
		{
			case HS.Targeting:
				HandleTargetingInput(@event);
				break;

			case HS.InHand:
				if (IsCancelEvent(@event))
				{
					GetViewport().SetInputAsHandled();
					ReturnToPlace();
				}
				break;
		}
	}

	private void HandleTargetingInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb && mb.Pressed)
		{
			if (mb.ButtonIndex == MouseButton.Left)
			{
				var hit = RaycastForTarget();
				if (hit != null)
				{
					GetViewport().SetInputAsHandled();
					SelectTarget(hit);
				}
			}
			else if (mb.ButtonIndex == MouseButton.Right)
			{
				GetViewport().SetInputAsHandled();
				CancelTargeting();
			}
		}
		else if (IsCancelEvent(@event))
		{
			GetViewport().SetInputAsHandled();
			CancelTargeting();
		}
	}

	private static bool IsCancelEvent(InputEvent e) =>
		e is InputEventKey key && key.Pressed && key.Keycode == Key.Escape;

	// ── Interaction ───────────────────────────────────────────────────────────

	private void OnInteracted()
	{
		if (_state != HS.Idle) return;
		_state = HS.Transitioning;

		_originalPosition = Position;
		_originalRotation = Rotation;

		if (_interactable != null) _interactable.Enabled = false;

		// Disable the PickupBody capsule so the player can't re-click during the animation.
		SetCollisionsEnabled(this, false);

		var camera = GetViewport().GetCamera3D();
		if (camera == null)
		{
			_state = HS.Idle;
			SetCollisionsEnabled(this, true);
			if (_interactable != null) _interactable.Enabled = _interactableEnabled;
			return;
		}

		_grabCamera = camera;
		// Defer StartGrab so SetCollisionsEnabled's deferred queue entries fire first.
		Callable.From(StartGrab).CallDeferred();
	}

	private void StartGrab()
	{
		if (!IsInsideTree() || _grabCamera == null || !IsInstanceValid(_grabCamera) || _handcuffModel == null)
		{
			_state = HS.Idle;
			SetCollisionsEnabled(this, true);
			if (_interactable != null) _interactable.Enabled = _interactableEnabled;
			_grabCamera = null;
			return;
		}

		// TopLevel=true makes the model's transform independent of the stationary root.
		// _handcuffModel has no StaticBody3D children (AutoGenerateCollision=false in .tscn),
		// so moving it is completely safe — no Jolt involvement.
		_handcuffModel.TopLevel = true;

		var tweenIn = CreateTween().SetParallel(true);
		tweenIn.TweenProperty(_handcuffModel, "global_position",
			_grabCamera.ToGlobal(HandPosition), TransitionTime)
			.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
		tweenIn.TweenProperty(_handcuffModel, "global_rotation",
			(_grabCamera.GlobalBasis * Basis.FromEuler(HandRotation)).GetEuler(),
			TransitionTime)
			.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
		tweenIn.Finished += () =>
		{
			_state = HS.InHand;
			Interactor.IsLocked = true;
			EmitSignal(SignalName.Grabbed);
		};
	}

	// ── Public API ────────────────────────────────────────────────────────────

	/// <summary>
	/// Enables or disables interaction. TableManager gates this to the local player's turn.
	/// The value is remembered so ReturnToPlace can restore it.
	/// </summary>
	public void SetInteractionEnabled(bool enabled)
	{
		_interactableEnabled = enabled;
		if (_interactable != null)
			_interactable.Enabled = enabled;
	}

	// ── Public targeting API ──────────────────────────────────────────────────

	public void BeginTargeting(List<SeatToken> targets)
	{
		if (_state != HS.InHand) return;
		_state = HS.Targeting;

		_targetTokens.Clear();
		if (targets != null) _targetTokens.AddRange(targets);

		foreach (var t in _targetTokens)
			if (IsInstanceValid(t)) t.SetTargetable(true);
	}

	public void CancelTargeting()
	{
		if (_state != HS.Targeting && _state != HS.InHand) return;
		DisableAllTargets();
		ReturnToPlace();
	}

	/// <summary>
	/// Flies the visual model from camera-hand position to the target player's head.
	/// Call after receiving the PlayerSelected signal.
	/// </summary>
	public void PlaceOnPlayerHead(Vector3 headWorldPos)
	{
		if (_state != HS.Flying) return;

		_grabCamera = null;   // stop _Process camera tracking
		_placedBasePos = headWorldPos;

		if (_handcuffModel == null)
		{
			_state = HS.Placed;
			Interactor.IsLocked = false;
			return;
		}

		float dist    = _handcuffModel.GlobalPosition.DistanceTo(_placedBasePos);
		float flyTime = Mathf.Clamp(dist * 0.12f, 0.4f, 0.9f);

		var tween = CreateTween().SetParallel(true);
		tween.TweenProperty(_handcuffModel, "global_position", _placedBasePos, flyTime)
		     .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
		tween.TweenProperty(_handcuffModel, "global_rotation", PlacedRotation, flyTime)
		     .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
		tween.Finished += () =>
		{
			_state = HS.Placed;
			Interactor.IsLocked = false;
			StartHoverLoop();
		};
	}

	/// <summary>
	/// Remote-client counterpart: shows and flies the model directly to the target head.
	/// Called by TableManager when handcuffs_applied is received.
	/// </summary>
	public void ApplyRemoteToHead(Vector3 headWorldPos)
	{
		if (_state == HS.Placed || _state == HS.Disappearing) return;

		Visible = true;
		Scale   = Vector3.One;
		SetCollisionsEnabled(this, false);
		_state = HS.Flying;

		if (_handcuffModel == null) return;

		_placedBasePos = headWorldPos;
		_handcuffModel.TopLevel = true;

		float dist    = _handcuffModel.GlobalPosition.DistanceTo(_placedBasePos);
		float flyTime = Mathf.Clamp(dist * 0.12f, 0.5f, 1.2f);

		var tween = CreateTween().SetParallel(true);
		tween.TweenProperty(_handcuffModel, "global_position", _placedBasePos, flyTime)
		     .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
		tween.TweenProperty(_handcuffModel, "global_rotation", PlacedRotation, flyTime)
		     .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
		tween.Finished += () =>
		{
			_state = HS.Placed;
			StartHoverLoop();
		};
	}

	/// <summary>
	/// Plays burn VFX, then resets the cuffs back to their hidden "fresh" state so a future
	/// golden-square grant can re-spawn them. Crucially does NOT QueueFree the node — the
	/// PlayerItemSet looks items up by name ("Handcuffs") in SpawnItem, so freeing the node
	/// would make any subsequent grant fail with "'Handcuffs' not found".
	/// </summary>
	public void BurnDisappear()
	{
		if (_state == HS.Disappearing) return;
		_state = HS.Disappearing;

		_grabCamera = null;
		_hoverTween?.Kill();
		_hoverTween = null;
		DisableAllTargets();
		Interactor.IsLocked = false;

		AddBurnFlash();
		AddEmbers();

		if (_handcuffModel != null && IsInstanceValid(_handcuffModel))
		{
			var tween = _handcuffModel.CreateTween();
			tween.TweenProperty(_handcuffModel, "scale", new Vector3(0.001f, 0.001f, 0.001f), 0.5f)
			     .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
			tween.Finished += ResetToFresh;
		}
		else
		{
			ResetToFresh();
		}
	}

	/// <summary>Restores the node to the state PlayerItemSet expects for a fresh (hidden) grant.</summary>
	private void ResetToFresh()
	{
		if (!IsInsideTree()) return;

		// Restore the model under the root so the next SpawnItem scale-in works correctly.
		if (_handcuffModel != null && IsInstanceValid(_handcuffModel))
		{
			_handcuffModel.TopLevel = false;
			_handcuffModel.Position = _modelOriginalLocalPos;
			_handcuffModel.Rotation = _modelOriginalLocalRot;
			_handcuffModel.Scale    = _modelOriginalLocalScale;
			_handcuffModel.Visible  = true;
		}

		// Hide the whole prop and disable interaction — matches PlayerItemSet.HideAndDisableAll.
		Visible = false;
		SetCollisionsEnabled(this, false);
		if (_interactable != null) _interactable.Enabled = false;
		_interactableEnabled = false;

		_state = HS.Idle;
	}

	// ── Private helpers ───────────────────────────────────────────────────────

	private void SelectTarget(SeatToken token)
	{
		DisableAllTargets();
		_state = HS.Flying;
		// Interactor stays locked — released when placed/burned/cancelled.
		EmitSignal(SignalName.PlayerSelected, token);
	}

	private void DisableAllTargets()
	{
		if (IsInstanceValid(_hoveredTarget))
			_hoveredTarget.ShowHandcuffPrompt(false);
		_hoveredTarget = null;

		foreach (var t in _targetTokens)
			if (IsInstanceValid(t)) t.SetTargetable(false);
		_targetTokens.Clear();
	}

	private SeatToken RaycastForTarget()
	{
		var camera = GetViewport().GetCamera3D();
		if (camera == null) return null;

		var mouse  = GetViewport().GetMousePosition();
		var origin = camera.ProjectRayOrigin(mouse);
		var end    = origin + camera.ProjectRayNormal(mouse) * 20f;

		var query = PhysicsRayQueryParameters3D.Create(origin, end);
		query.CollideWithAreas  = false;
		query.CollideWithBodies = true;
		var result = GetWorld3D().DirectSpaceState.IntersectRay(query);

		if (result.Count == 0) return null;

		var collider = result["collider"].As<Node>();
		foreach (var token in _targetTokens)
			if (IsInstanceValid(token) && token.IsHitBy(collider)) return token;

		return null;
	}

	private void ReturnToPlace()
	{
		if (_state == HS.Idle || _state == HS.Disappearing) return;

		_hoverTween?.Kill();
		_grabCamera = null;
		_state = HS.Transitioning;

		if (_handcuffModel == null)
		{
			_state = HS.Idle;
			SetCollisionsEnabled(this, true);
			Interactor.IsLocked = false;
			if (_interactable != null) _interactable.Enabled = _interactableEnabled;
			return;
		}

		// Root node hasn't moved, so the world target is just ToGlobal of the original local pos.
		Vector3 targetWorldPos = ToGlobal(_modelOriginalLocalPos);
		Vector3 targetWorldRot = (GlobalBasis * Basis.FromEuler(_modelOriginalLocalRot)).GetEuler();

		var tween = CreateTween().SetParallel(true);
		tween.TweenProperty(_handcuffModel, "global_position", targetWorldPos, TransitionTime)
		     .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		tween.TweenProperty(_handcuffModel, "global_rotation", targetWorldRot, TransitionTime)
		     .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		tween.Finished += () =>
		{
			// Snap back to local space under the root.
			_handcuffModel.TopLevel = false;
			_handcuffModel.Position = _modelOriginalLocalPos;
			_handcuffModel.Rotation = _modelOriginalLocalRot;
			_handcuffModel.Scale    = _modelOriginalLocalScale;
			_state = HS.Idle;
			SetCollisionsEnabled(this, true);
			Interactor.IsLocked = false;
			if (_interactable != null) _interactable.Enabled = _interactableEnabled;
		};
	}

	private void StartHoverLoop()
	{
		if (_handcuffModel == null) return;
		_hoverTween?.Kill();
		_hoverTween = CreateTween().SetLoops();
		_hoverTween.TweenProperty(_handcuffModel, "global_position:y", _placedBasePos.Y + 0.07f, 0.65f)
		           .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		_hoverTween.TweenProperty(_handcuffModel, "global_position:y", _placedBasePos.Y - 0.02f, 0.65f)
		           .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
	}

	// ── VFX ───────────────────────────────────────────────────────────────────

	private void AddBurnFlash()
	{
		Vector3 pos = _handcuffModel != null ? _handcuffModel.GlobalPosition : GlobalPosition;
		var flash = new OmniLight3D
		{
			TopLevel    = true,
			LightColor  = _burnColor,
			LightEnergy = 0f,
			OmniRange   = 5f,
		};
		GetParent().AddChild(flash);
		flash.GlobalPosition = pos + Vector3.Up * 0.1f;

		// Bind the fade tween to the flash itself, not to this Handcuffs node — the
		// cuffs QueueFree mid-fade, which would kill the tween and orphan a dim,
		// never-freed light lingering on the table.
		var t = flash.CreateTween();
		t.TweenProperty(flash, "light_energy", 5f, 0.1f);
		t.TweenProperty(flash, "light_energy", 0f, 0.5f);
		t.Finished += () => flash.QueueFree();
	}

	private void AddEmbers()
	{
		Vector3 pos = _handcuffModel != null ? _handcuffModel.GlobalPosition : GlobalPosition;
		var particles = new CpuParticles3D { TopLevel = true };
		GetParent().AddChild(particles);
		particles.GlobalPosition     = pos + Vector3.Up * 0.1f;
		particles.Amount             = 100;
		particles.Lifetime           = 0.9f;
		particles.OneShot            = true;
		particles.Explosiveness      = 0.85f;
		particles.EmissionShape      = CpuParticles3D.EmissionShapeEnum.Box;
		particles.EmissionBoxExtents = new Vector3(0.2f, 0.3f, 0.2f);
		particles.Direction          = new Vector3(0f, 1f, 0f);
		particles.Spread             = 55f;
		particles.Gravity            = new Vector3(0f, 2f, 0f);
		particles.InitialVelocityMin = 0.8f;
		particles.InitialVelocityMax = 2.2f;
		particles.ScaleAmountMin     = 0.7f;
		particles.ScaleAmountMax     = 1.3f;

		var gradient = new Gradient();
		gradient.SetColor(0, new Color(1f, 1f, 0.5f, 1f));
		gradient.AddPoint(0.3f, new Color(1f, 0.5f, 0.1f, 0.9f));
		gradient.SetColor(gradient.GetPointCount() - 1, new Color(0.8f, 0.1f, 0f, 0f));
		particles.ColorRamp = gradient;

		particles.Mesh = new QuadMesh { Size = new Vector2(0.017f, 0.017f) };
		particles.MaterialOverride = new StandardMaterial3D
		{
			ShadingMode            = StandardMaterial3D.ShadingModeEnum.Unshaded,
			VertexColorUseAsAlbedo = true,
			BillboardMode          = StandardMaterial3D.BillboardModeEnum.Enabled,
			Transparency           = StandardMaterial3D.TransparencyEnum.Alpha,
		};
		particles.Emitting = true;

		GetTree().CreateTimer(particles.Lifetime + 0.5f).Timeout +=
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
