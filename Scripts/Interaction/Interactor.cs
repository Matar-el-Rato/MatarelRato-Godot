using Godot;
using System;

public partial class Interactor : Node3D
{
	[Export] public float InteractionRange = 2.0f;
	[Export] public NodePath PromptLabelPath;

	public static bool IsLocked { get; set; } = false;

	private RayCast3D _rayCast;
	private Control _promptLabel;
	private Label _textLabel;
	private Label _keyLabel;
	private IInteractable _currentInteractable;
	private Tween _promptTween;

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
				_keyLabel = _promptLabel.GetNodeOrNull<Label>("KeyLabel");
				
				// Initialize animation state
				_promptLabel.Modulate = new Color(1, 1, 1, 0);
				_promptLabel.Scale = new Vector2(0.8f, 0.8f);
				_promptLabel.Visible = false;
			}
		}
		
		UpdatePrompt(false);
	}

	private bool _isLeftClickHeld = false;

	public override void _Process(double delta)
	{
		if (IsLocked)
		{
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
			
			// 1. Check for specific interaction key (Default "E")
			if ((!string.IsNullOrEmpty(iNode.InteractionAction) && Input.IsActionJustPressed(iNode.InteractionAction)) || Input.IsActionJustPressed("interact"))
			{
				wantsToInteract = true;
			}
			// 2. Check for left-click if enabled for this interactable
			else if (iNode.UseLeftClick)
			{
				bool isPressed = Input.IsMouseButtonPressed(MouseButton.Left);
				if (isPressed && !_isLeftClickHeld)
				{
					wantsToInteract = true; 
				}
				_isLeftClickHeld = isPressed;
			}
			
			if (wantsToInteract)
			{
				GD.Print($"[Interactor] Interacting with: {iNode.GetParent().Name} via {iNode.Name}");
				iNode.Interact();
			}
		}
		else
		{
			_isLeftClickHeld = Input.IsMouseButtonPressed(MouseButton.Left);
		}
	}

	private void CheckInteraction()
	{
		if (_rayCast.IsColliding())
		{
			var collider = _rayCast.GetCollider() as Node;
			if (collider == null) return;

			IInteractable interactable = null;
			Node current = collider;
			
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
				{
					interactable = iInterface;
				}

				if (interactable == null)
				{
					current = current.GetParent();
				}
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

	private void UpdatePrompt(bool visible)
	{
		if (_promptLabel == null) return;

		if (_promptTween != null)
		{
			_promptTween.Kill();
		}

		_promptTween = CreateTween();
		
		if (visible && _currentInteractable is Interactable iNode)
		{
			if (_textLabel != null)
			{
				_textLabel.Text = iNode.PromptText;
			}

			if (_keyLabel != null)
			{
				if (iNode.UseLeftClick)
				{
					_keyLabel.Visible = false;
				}
				else
				{
					_keyLabel.Visible = true;
					string action = (iNode.InteractionAction ?? "").Trim().ToLower();
					bool isDefaultAction = string.IsNullOrWhiteSpace(action) || action == "interact";
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
			
			_promptTween.Chain().TweenCallback(Callable.From(() => {
				if (_promptLabel.Modulate.A < 0.05f) _promptLabel.Visible = false;
			}));
		}
	}
}
