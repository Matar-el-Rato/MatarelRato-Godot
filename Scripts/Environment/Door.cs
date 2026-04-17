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
	[Export] public float    OpenAngle         = 120.0f;
	[Export] public float    AnimationDuration = 0.5f;
	/// <summary>Optional path to a paired Door node that mirrors this one.</summary>
	[Export] public NodePath LinkedDoorPath;
	/// <summary>When true, the door cannot be opened unless the player is logged in.</summary>
	[Export] public bool     RequiresLogin     = false;
	/// <summary>Plays when the door opens. Falls back to the shared SFX asset if null.</summary>
	[Export] public AudioStream OpenSound;
	/// <summary>Plays when the door closes. Falls back to the shared SFX asset if null.</summary>
	[Export] public AudioStream CloseSound;

	private bool    _isOpen            = false;
	private Vector3 _closedRotation;
	private Vector3 _openRotation;
	private Door    _linkedDoor;
	private bool    _isProcessingLinked = false;
	private bool    _isAnimating        = false;

	private Interactable        _interactable;
	private string              _originalPromptText;
	private Color               _originalHighlightColor;
	private AudioStreamPlayer3D _audio;

	private static readonly Color LockedHighlightColor = Colors.Red;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_closedRotation = Rotation;
		_openRotation   = new Vector3(Rotation.X, Rotation.Y + Mathf.DegToRad(OpenAngle), Rotation.Z);

		if (LinkedDoorPath != null && !LinkedDoorPath.IsEmpty)
			_linkedDoor = GetNodeOrNull<Door>(LinkedDoorPath);

		// Auto-load sounds from the shared SFX folder if not set in the Inspector.
		OpenSound  ??= ResourceLoader.Load<AudioStream>("res://Assets/Sound FX/open_door.wav");
		CloseSound ??= ResourceLoader.Load<AudioStream>("res://Assets/Sound FX/close_door.wav");

		_audio             = new AudioStreamPlayer3D();
		_audio.UnitSize    = 5.0f;
		_audio.MaxDistance = 15.0f;
		AddChild(_audio);

		_interactable = GetNodeOrNull<Interactable>("Interactable");
		if (_interactable != null)
		{
			_originalPromptText     = _interactable.PromptText;
			_originalHighlightColor = _interactable.HighlightColor;
			_interactable.Interacted += OnInteracted;
			_interactable.Focused    += OnFocused;
			_interactable.Unfocused  += OnUnfocused;
		}
	}

	// ── Focus propagation ─────────────────────────────────────────────────────

	private void OnFocused()
	{
		UpdateAuthVisuals();
		if (_linkedDoor == null || _isProcessingLinked) return;
		_isProcessingLinked = true;
		_linkedDoor.SetFocus(true);
		_isProcessingLinked = false;
	}

	private void UpdateAuthVisuals()
	{
		if (_interactable == null || !RequiresLogin) return;
		bool loggedIn = AuthManager.IsLoggedIn;
		_interactable.SetHighlightColor(loggedIn ? _originalHighlightColor : LockedHighlightColor);
		_interactable.PromptText = loggedIn ? _originalPromptText : "You must identify first";
	}

	private void OnUnfocused()
	{
		if (_linkedDoor == null || _isProcessingLinked) return;
		_isProcessingLinked = true;
		_linkedDoor.SetFocus(false);
		_isProcessingLinked = false;
	}

	public void SetFocus(bool focused)
	{
		if (focused) _interactable?.OnFocus();
		else         _interactable?.OnBlur();
	}

	// ── Interaction ───────────────────────────────────────────────────────────

	private void OnInteracted()
	{
		if (RequiresLogin && !AuthManager.IsLoggedIn) return;
		Interact(playSound: true);
	}

	/// <summary>Closes the door if it is currently open. Used by RoomJoin on room exit.</summary>
	public void Close()
	{
		if (_isOpen) Interact(playSound: true);
	}

	/// <summary>
	/// Toggles the door open or closed with a smooth tween.
	/// Also triggers the linked door simultaneously (silently, to avoid double audio).
	/// </summary>
	public void Interact(bool playSound = true)
	{
		if (_isAnimating) return;

		_isOpen      = !_isOpen;
		_isAnimating = true;

		if (playSound && _audio != null)
		{
			_audio.Stream = _isOpen ? OpenSound : CloseSound;
			_audio.Play();
		}

		Vector3 targetRotation = _isOpen ? _openRotation : _closedRotation;

		Tween tween = GetTree().CreateTween();
		tween.TweenProperty(this, "rotation", targetRotation, AnimationDuration)
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.Out);
		tween.Finished += () => _isAnimating = false;

		// Drive the linked door silently — it shares the audio from this door.
		if (_linkedDoor != null && !_isProcessingLinked)
		{
			_isProcessingLinked = true;
			_linkedDoor.Interact(playSound: false);
			_isProcessingLinked = false;
		}
	}
}
