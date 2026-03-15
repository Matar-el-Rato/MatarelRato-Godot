// ═══════════════════════════════════════════════════
// Interactor.cs
// Player-attached node that casts a ray forward and
// drives focus/unfocus and interaction on IInteractable objects.
// Shows an animated HUD prompt when a target is in range.
// ═══════════════════════════════════════════════════
using Godot;
using System;

/// <summary>
/// Attached to the player. Uses a <see cref="RayCast3D"/> to detect
/// <see cref="IInteractable"/> objects in range and triggers focus highlights,
/// HUD prompts, and interaction on key/click input.
/// Set <see cref="IsLocked"/> to true to globally suppress all interactions
/// (e.g. while a clipboard or cutscene is active).
/// </summary>
public partial class Interactor : Node3D
{
	[Export] public float    InteractionRange = 5.0f;
	[Export] public NodePath PromptLabelPath;

	/// <summary>
	/// When true, the Interactor ignores all input and clears any active target.
	/// Used by FocusController, CubileteController, etc. to prevent conflicting interactions.
	/// </summary>
	public static bool IsLocked { get; set; } = false;

	private RayCast3D    _rayCast;
	private Control      _promptLabel;
	private Label        _textLabel;
	private Label        _keyLabel;
	private IInteractable _currentInteractable;
	private Tween        _promptTween;

	// Tracks whether left-mouse was held last frame to detect fresh presses.
	private bool _isLeftClickHeld = false;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_rayCast = GetNode<RayCast3D>("RayCast3D");
		_rayCast.TargetPosition = new Vector3(0, 0, -InteractionRange);

		if (!PromptLabelPath.IsEmpty)
		{
			_promptLabel = GetNodeOrNull<Control>(PromptLabelPath);
			if (_promptLabel != null)
			{
				_textLabel = _promptLabel.GetNodeOrNull<Label>("PromptLabel");
				_keyLabel  = _promptLabel.GetNodeOrNull<Label>("KeyLabel");

				// Start invisible and slightly scaled down for the pop-in animation.
				_promptLabel.Modulate = new Color(1, 1, 1, 0);
				_promptLabel.Scale    = new Vector2(0.8f, 0.8f);
				_promptLabel.Visible  = false;
			}
		}

		UpdatePrompt(false);
	}

	// ── Per-frame ─────────────────────────────────────────────────────────────

	public override void _Process(double delta)
	{
		if (IsLocked)
		{
			// Clear any lingering target when locked (e.g. focus was grabbed mid-hover).
			if (_currentInteractable != null)
			{
				_currentInteractable.OnBlur();
				_currentInteractable = null;
				UpdatePrompt(false);
			}
			return;
		}

		CheckInteraction();

		if (_currentInteractable is Interactable iNode)
		{
			bool wantsToInteract = false;

			// Priority 1: dedicated action key or the global "interact" (E) action.
			if ((!string.IsNullOrEmpty(iNode.InteractionAction) && Input.IsActionJustPressed(iNode.InteractionAction))
				|| Input.IsActionJustPressed("interact"))
			{
				wantsToInteract = true;
			}
			// Priority 2: left-click, if this interactable opts in.
			else if (iNode.UseLeftClick)
			{
				bool isPressed = Input.IsMouseButtonPressed(MouseButton.Left);
				if (isPressed && !_isLeftClickHeld)
					wantsToInteract = true;
				_isLeftClickHeld = isPressed;
			}

			if (wantsToInteract)
				iNode.Interact();
		}
		else
		{
			// Keep held-state in sync even when no interactable is targeted.
			_isLeftClickHeld = Input.IsMouseButtonPressed(MouseButton.Left);
		}
	}

	// ── Raycast ───────────────────────────────────────────────────────────────

	/// <summary>
	/// Walks up the collision hierarchy from the raycast hit to find an
	/// <see cref="IInteractable"/> component, then fires focus events when
	/// the target changes.
	/// </summary>
	private void CheckInteraction()
	{
		if (_rayCast.IsColliding())
		{
			var collider = _rayCast.GetCollider() as Node;
			if (collider == null) return;

			IInteractable interactable = null;
			Node current = collider;

			// Walk up the tree: check children of each node for an Interactable,
			// then check the node itself, then move to its parent.
			while (current != null && interactable == null)
			{
				foreach (var child in current.GetChildren(true))
				{
					if (child is Interactable iNode)
					{
						interactable = iNode;
						break;
					}
				}

				if (interactable == null && current is IInteractable iInterface)
					interactable = iInterface;

				if (interactable == null)
					current = current.GetParent();
			}

			if (interactable != _currentInteractable)
			{
				_currentInteractable?.OnBlur();
				_currentInteractable = interactable;
				_currentInteractable?.OnFocus();
				UpdatePrompt(_currentInteractable != null);
			}
		}
		else if (_currentInteractable != null)
		{
			_currentInteractable.OnBlur();
			_currentInteractable = null;
			UpdatePrompt(false);
		}
	}

	// ── HUD prompt ────────────────────────────────────────────────────────────

	/// <summary>
	/// Animates the prompt label into or out of view. When shown, populates the
	/// text and key hint from the target <see cref="Interactable"/>.
	/// </summary>
	private void UpdatePrompt(bool visible)
	{
		if (_promptLabel == null) return;

		_promptTween?.Kill();
		_promptTween = CreateTween();

		if (visible && _currentInteractable is Interactable iNode)
		{
			if (_textLabel != null)
				_textLabel.Text = iNode.PromptText;

			if (_keyLabel != null)
			{
				if (iNode.UseLeftClick)
				{
					// Left-click interactables show no key hint — clicking is implied.
					_keyLabel.Visible = false;
				}
				else
				{
					_keyLabel.Visible = true;
					string action        = (iNode.InteractionAction ?? "").Trim().ToLower();
					bool   isDefaultAction = string.IsNullOrWhiteSpace(action) || action == "interact";
					_keyLabel.Text = isDefaultAction ? "E" : action.ToUpper();
				}
			}

			_promptLabel.Visible = true;
			_promptTween.SetParallel(true);
			_promptTween.TweenProperty(_promptLabel, "modulate:a", 1.0f, 0.15f)
				.SetTrans(Tween.TransitionType.Quad)
				.SetEase(Tween.EaseType.Out);
			_promptTween.TweenProperty(_promptLabel, "scale", Vector2.One, 0.15f)
				.SetTrans(Tween.TransitionType.Back)
				.SetEase(Tween.EaseType.Out);
		}
		else
		{
			_promptTween.SetParallel(true);
			_promptTween.TweenProperty(_promptLabel, "modulate:a", 0.0f, 0.1f)
				.SetTrans(Tween.TransitionType.Quad)
				.SetEase(Tween.EaseType.In);
			_promptTween.TweenProperty(_promptLabel, "scale", new Vector2(0.8f, 0.8f), 0.1f)
				.SetTrans(Tween.TransitionType.Quad)
				.SetEase(Tween.EaseType.In);

			// Hide the node after the fade completes to save draw calls.
			_promptTween.Chain().TweenCallback(Callable.From(() => {
				if (_promptLabel.Modulate.A < 0.05f) _promptLabel.Visible = false;
			}));
		}
	}
}
