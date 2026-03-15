// ═══════════════════════════════════════════════════
// Door.cs
// Interactable door that rotates open/closed on interaction.
// Supports a linked door that mirrors the interaction
// (e.g. a double door pair that both swing together).
// ═══════════════════════════════════════════════════
using Godot;
using System;

/// <summary>
/// Rotates a door prop around its Y-axis when interacted with.
/// If <see cref="LinkedDoorPath"/> is set, the linked door will also
/// open or close simultaneously and share highlight focus events.
/// </summary>
public partial class Door : Node3D
{
	/// <summary>How far the door swings open, in degrees.</summary>
	[Export] public float    OpenAngle        = 120.0f;
	[Export] public float    AnimationDuration = 0.5f;
	/// <summary>Optional path to a paired Door node that mirrors this one.</summary>
	[Export] public NodePath LinkedDoorPath;

	private bool  _isOpen            = false;
	private Vector3 _closedRotation;
	private Vector3 _openRotation;
	private Door  _linkedDoor;
	// Guards against infinite recursion when syncing linked doors.
	private bool  _isProcessingLinked = false;
	private bool  _isAnimating        = false;

	private Interactable _interactable;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		// Record closed rotation from the scene placement; open is offset by OpenAngle.
		_closedRotation = Rotation;
		_openRotation   = new Vector3(Rotation.X, Rotation.Y + Mathf.DegToRad(OpenAngle), Rotation.Z);

		if (LinkedDoorPath != null && !LinkedDoorPath.IsEmpty)
			_linkedDoor = GetNodeOrNull<Door>(LinkedDoorPath);

		_interactable = GetNodeOrNull<Interactable>("Interactable");
		if (_interactable != null)
		{
			_interactable.Interacted += OnInteracted;
			_interactable.Focused    += OnFocused;
			_interactable.Unfocused  += OnUnfocused;
		}
	}

	// ── Focus propagation ─────────────────────────────────────────────────────

	/// <summary>Propagates the focus highlight to the linked door, if any.</summary>
	private void OnFocused()
	{
		if (_linkedDoor == null || _isProcessingLinked) return;
		_isProcessingLinked = true;
		_linkedDoor.SetFocus(true);
		_isProcessingLinked = false;
	}

	private void OnUnfocused()
	{
		if (_linkedDoor == null || _isProcessingLinked) return;
		_isProcessingLinked = true;
		_linkedDoor.SetFocus(false);
		_isProcessingLinked = false;
	}

	/// <summary>
	/// Called by the linked door to apply or remove the focus highlight on this door.
	/// </summary>
	public void SetFocus(bool focused)
	{
		if (focused) _interactable?.OnFocus();
		else         _interactable?.OnBlur();
	}

	// ── Interaction ───────────────────────────────────────────────────────────

	private void OnInteracted() => Interact();

	/// <summary>
	/// Toggles the door open or closed with a smooth tween.
	/// Also triggers the linked door simultaneously.
	/// </summary>
	public void Interact()
	{
		if (_isAnimating) return;

		_isOpen      = !_isOpen;
		_isAnimating = true;

		Vector3 targetRotation = _isOpen ? _openRotation : _closedRotation;

		Tween tween = GetTree().CreateTween();
		tween.TweenProperty(this, "rotation", targetRotation, AnimationDuration)
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.Out);
		tween.Finished += () => _isAnimating = false;

		// Sync linked door without waiting for this tween to finish.
		if (_linkedDoor != null && !_isProcessingLinked)
		{
			_isProcessingLinked = true;
			_linkedDoor.Interact();
			_isProcessingLinked = false;
		}
	}
}
