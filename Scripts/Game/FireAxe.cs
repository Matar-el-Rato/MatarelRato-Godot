// ═══════════════════════════════════════════════════
// FireAxe.cs
// Interactable fire axe prop: player picks it up (tweens to camera),
// clicks to "use" (placeholder — returns immediately until server action
// is implemented), then auto-returns to the table.
// ═══════════════════════════════════════════════════
using Godot;

/// <summary>
/// Grab → click → return lifecycle, identical in structure to Handcuffs.cs.
/// Left-click while held returns the item (placeholder for targeting an
/// opponent barrier once networking is wired).
/// </summary>
public partial class FireAxe : Node3D
{
	[ExportGroup("Hand Positioning")]
	[Export] public Vector3 HandPosition   = new Vector3(0.15f, -0.15f, -0.35f);
	[Export] public Vector3 HandRotation   = new Vector3(0f, Mathf.Pi, 0f);
	[Export] public float   TransitionTime = 0.5f;

	[ExportGroup("Components")]
	[Export] public NodePath InteractablePath;

	private Interactable _interactable;

	private Vector3 _originalPosition;
	private Vector3 _originalRotation;
	private Node    _originalParent;

	private bool _isInHand        = false;
	private bool _isReturning     = false;
	private bool _isTransitioning = false;
	private bool _isDisappearing  = false;

	// Initial transform/parent captured at _Ready so a consumed axe resets back to the
	// hidden "fresh" pose PlayerItemSet expects for a future SpawnItem grant — even if
	// it was never grabbed on this client (remote-attacker view).
	private Node    _initialParent;
	private Vector3 _initialLocalPos;
	private Vector3 _initialLocalRot;
	private Vector3 _initialLocalScale = Vector3.One;

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

	/// <summary>
	/// Enable/disable interaction. TableManager gates this around the appropriate window
	/// once the break-barrier logic is wired.
	/// </summary>
	public void SetInteractionEnabled(bool enabled)
	{
		if (_interactable != null)
			_interactable.Enabled = enabled;
	}

	// ── Input ─────────────────────────────────────────────────────────────────

	public override void _Input(InputEvent @event)
	{
		if (_isInHand && !_isReturning && !_isTransitioning)
		{
			if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
				ReturnToPlace();
		}
	}

	// ── Interaction ───────────────────────────────────────────────────────────

	private void OnInteracted()
	{
		if (_isInHand || _isTransitioning || _isReturning) return;
		_isTransitioning = true;

		_originalParent   = GetParent();
		_originalPosition = Position;
		_originalRotation = Rotation;

		SetCollisionsEnabled(this, false);

		var camera = GetViewport().GetCamera3D();
		if (camera != null)
			ReparentToHand(camera);
		else
		{
			_isTransitioning = false;
			SetCollisionsEnabled(this, true);
		}
	}

	// ── Pick-up / Return ──────────────────────────────────────────────────────

	private void ReparentToHand(Node3D handParent)
	{
		Reparent(handParent, true);

		var tween = CreateTween();
		tween.SetParallel(true);
		tween.TweenProperty(this, "position", HandPosition, TransitionTime)
			.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
		tween.TweenProperty(this, "rotation", HandRotation, TransitionTime)
			.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);

		tween.Finished += () =>
		{
			_isInHand        = true;
			_isTransitioning = false;
			Interactor.IsLocked = true;
		};
	}

	private void ReturnToPlace()
	{
		_isReturning = true;
		_isInHand    = false;

		Reparent(_originalParent, true);

		var tween = CreateTween();
		tween.SetParallel(true);
		tween.TweenProperty(this, "position", _originalPosition, TransitionTime)
			.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		tween.TweenProperty(this, "rotation", _originalRotation, TransitionTime)
			.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);

		tween.Finished += () =>
		{
			_isReturning = false;
			SetCollisionsEnabled(this, true);
			Interactor.IsLocked = false;
		};
	}

	// ── Consume ───────────────────────────────────────────────────────────────

	/// <summary>
	/// Scales the axe out and resets it to its hidden "fresh" state so a future
	/// golden-square grant via PlayerItemSet.SpawnItem can re-spawn it. Crucially does NOT
	/// QueueFree — PlayerItemSet looks the node up by name ("FireAxe") in SpawnItem, so
	/// freeing it would make any subsequent grant fail with "not found in PlayerItemSet".
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

		// If the axe was reparented to a hand during use, return it under the PlayerItemSet
		// so SpawnItem's name lookup succeeds on the next grant.
		if (_initialParent != null && IsInstanceValid(_initialParent) && GetParent() != _initialParent)
			Reparent(_initialParent, true);

		Position = _initialLocalPos;
		Rotation = _initialLocalRot;
		Scale    = _initialLocalScale;

		Visible = false;
		if (_interactable != null) _interactable.Enabled = false;

		_isInHand        = false;
		_isReturning     = false;
		_isTransitioning = false;
		_isDisappearing  = false;
		Interactor.IsLocked = false;
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
