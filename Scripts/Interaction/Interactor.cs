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

	public override void _Process(double delta)
	{
		CheckInteraction();

		if (_currentInteractable is Interactable iNode)
		{
			if (iNode.UseLeftClick)
			{
				if (Input.IsActionJustPressed("mouse_left")) 
				{
					iNode.Interact();
				}
			}
			else if (Input.IsActionJustPressed(iNode.InteractionAction))
			{
				iNode.Interact();
			}
		}
		else if (Input.IsActionJustPressed("interact"))
		{
			_currentInteractable?.Interact();
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
				foreach (var child in current.GetChildren())
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
