using Godot;
using System;

public partial class Interactor : Node3D
{
	[Export] public float InteractionRange = 2.0f;
	[Export] public NodePath PromptLabelPath;

	private RayCast3D _rayCast;
	private Control _promptLabel;
	private IInteractable _currentInteractable;

	public override void _Ready()
	{
		_rayCast = GetNode<RayCast3D>("RayCast3D");
		_rayCast.TargetPosition = new Vector3(0, 0, -InteractionRange);
		
		if (!PromptLabelPath.IsEmpty)
		{
			_promptLabel = GetNodeOrNull<Control>(PromptLabelPath);
			GD.Print($"Interactor: Found prompt node: {_promptLabel != null}");
		}
		
		UpdatePrompt(false);
	}

	public override void _Process(double delta)
	{
		CheckInteraction();

		if (Input.IsActionJustPressed("interact"))
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

			// Search up the hierarchy for an IInteractable
			IInteractable interactable = null;
			Node current = collider;
			
			while (current != null && interactable == null)
			{
				if (current is IInteractable i)
				{
					interactable = i;
				}
				else
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
			_promptLabel.Visible = visible;
		}
	}
}
