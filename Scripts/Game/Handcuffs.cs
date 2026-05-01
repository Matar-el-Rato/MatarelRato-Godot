// ═══════════════════════════════════════════════════
// Handcuffs.cs
// Interactable handcuffs prop: player picks them up (tweens to camera),
// clicks to "use" (placeholder — returns immediately until server action
// is implemented), then auto-returns to the table.
// ═══════════════════════════════════════════════════
using Godot;

/// <summary>
/// Grab → click → return lifecycle, identical in structure to Gun.cs
/// but without shoot logic. Left-click while held returns the item
/// (placeholder for targeting an opponent once networking is wired).
/// </summary>
public partial class Handcuffs : Node3D
{
	[ExportGroup("Hand Positioning")]
	[Export] public Vector3 HandPosition   = new Vector3(0.1f, -0.08f, -0.25f);
	[Export] public Vector3 HandRotation   = new Vector3(0f, Mathf.Pi / 2f, 0f);
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

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_interactable = GetNodeOrNull<Interactable>(InteractablePath);
		if (_interactable != null)
			_interactable.Interacted += OnInteracted;
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
