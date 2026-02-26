using Godot;
using System;

public partial class Door : Node3D
{
	[Export] public float OpenAngle = 120.0f;
	[Export] public float AnimationDuration = 0.5f;
	[Export] public NodePath LinkedDoorPath;

	private bool _isOpen = false;
	private Vector3 _closedRotation;
	private Vector3 _openRotation;
	private Door _linkedDoor;
	private bool _isProcessingLinked = false;
	private bool _isAnimating = false;
	
	private Interactable _interactable;

	public override void _Ready()
	{
		_closedRotation = Rotation;
		_openRotation = new Vector3(Rotation.X, Rotation.Y + Mathf.DegToRad(OpenAngle), Rotation.Z);
		
		if (LinkedDoorPath != null && !LinkedDoorPath.IsEmpty)
		{
			_linkedDoor = GetNodeOrNull<Door>(LinkedDoorPath);
		}

		// Connect to child Interactable if it exists
		_interactable = GetNodeOrNull<Interactable>("Interactable");
		if (_interactable != null)
		{
			_interactable.Interacted += OnInteracted;
			_interactable.Focused += OnFocused;
			_interactable.Unfocused += OnUnfocused;
		}
	}

	private void OnFocused()
	{
		if (_linkedDoor != null && !_isProcessingLinked)
		{
			_isProcessingLinked = true;
			_linkedDoor.SetFocus(true);
			_isProcessingLinked = false;
		}
	}

	private void OnUnfocused()
	{
		if (_linkedDoor != null && !_isProcessingLinked)
		{
			_isProcessingLinked = true;
			_linkedDoor.SetFocus(false);
			_isProcessingLinked = false;
		}
	}

	public void SetFocus(bool focused)
	{
		if (focused) _interactable?.OnFocus();
		else _interactable?.OnBlur();
	}

	private void OnInteracted()
	{
		Interact();
	}

	public void Interact()
	{
		if (_isAnimating) return;

		_isOpen = !_isOpen;
		_isAnimating = true;

		Vector3 targetRotation = _isOpen ? _openRotation : _closedRotation;
		
		Tween tween = GetTree().CreateTween();
		tween.TweenProperty(this, "rotation", targetRotation, AnimationDuration)
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.Out);
		
		tween.Finished += () => _isAnimating = false;

		// Sync with linked door immediately (no delay)
		if (_linkedDoor != null && !_isProcessingLinked)
		{
			_isProcessingLinked = true;
			_linkedDoor.Interact();
			_isProcessingLinked = false;
		}
	}
}
