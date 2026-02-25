using Godot;
using System;

public partial class Interactor : Node3D
{
	[Export] public float InteractionRange = 2.0f;
	[Export] public NodePath PromptLabelPath;

	private RayCast3D _rayCast;
	private Control _promptLabel;
	private Label _textLabel;
	private IInteractable _currentInteractable;

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
			}
		}
		
		UpdatePrompt(false);
	}

	private bool _isLeftClickHeld = false;

	public override void _Process(double delta)
	{
		CheckInteraction();

		if (_currentInteractable is Interactable iNode)
		{
			bool wantsToInteract = false;
			
			// 1. Check for specific interaction key (Default "E")
			if (Input.IsActionJustPressed(iNode.InteractionAction) || Input.IsActionJustPressed("interact"))
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
		if (_promptLabel != null)
		{
			bool shouldShow = visible;
			if (visible && _currentInteractable is Interactable interactableNode && interactableNode.UseLeftClick)
			{
				shouldShow = false;
			}

			_promptLabel.Visible = shouldShow;
			
			if (shouldShow && _textLabel != null)
			{
				_textLabel.Text = "E";
			}
		}
	}
}
