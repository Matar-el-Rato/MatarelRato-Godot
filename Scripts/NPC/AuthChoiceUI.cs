// ═══════════════════════════════════════════════════
// AuthChoiceUI.cs
// 3D in-world panel with Login / Register buttons near Pigga.
// Shown and hidden by NPC; each option is an Interactable child.
// ═══════════════════════════════════════════════════
using Godot;
using System;

/// <summary>
/// 3D choice panel with two flickering buttons (Login / Register).
/// Positioned in the world near Pigga. Shown/hidden by NPC.
/// Each button is an Interactable child — raycasting + left-click.
/// </summary>
public partial class AuthChoiceUI : Node3D
{
	/// <summary>Global singleton — set in _EnterTree, cleared in _ExitTree.</summary>
	public static AuthChoiceUI Instance { get; private set; }

	[Export] public NodePath LoginLabelPath    = "LoginOption/Label3D";
	[Export] public NodePath RegisterLabelPath = "RegisterOption/Label3D";
	[Export] public NodePath LoginInteractablePath    = "LoginOption/Interactable";
	[Export] public NodePath RegisterInteractablePath = "RegisterOption/Interactable";

	private Label3D      _loginLabel;
	private Label3D      _registerLabel;
	private Interactable _loginInteractable;
	private Interactable _registerInteractable;

	private Action _onLoginChosen;
	private Action _onRegisterChosen;

	private bool _loginHovered    = false;
	private bool _registerHovered = false;

	private Random _rng = new Random();
	private AudioStreamPlayer3D _flickerAudio;

	// Cached world position used as the animation origin for show/hide slides.
	private Vector3 _storedPosition;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _EnterTree()
	{
		Instance = this;
	}

	public override void _Ready()
	{
		_loginLabel           = GetNodeOrNull<Label3D>(LoginLabelPath);
		_registerLabel        = GetNodeOrNull<Label3D>(RegisterLabelPath);
		_loginInteractable    = GetNodeOrNull<Interactable>(LoginInteractablePath);
		_registerInteractable = GetNodeOrNull<Interactable>(RegisterInteractablePath);

		if (_loginInteractable != null)
		{
			_loginInteractable.Interacted += OnLoginPressed;
			_loginInteractable.Focused    += () => _loginHovered = true;
			_loginInteractable.Unfocused  += () => _loginHovered = false;
		}
		if (_registerInteractable != null)
		{
			_registerInteractable.Interacted += OnRegisterPressed;
			_registerInteractable.Focused    += () => _registerHovered = true;
			_registerInteractable.Unfocused  += () => _registerHovered = false;
		}

		_flickerAudio   = GetNodeOrNull<AudioStreamPlayer3D>("FlickerAudio");
		_storedPosition = Position;

		Visible = false;
		StartFlicker();
	}

	public override void _ExitTree()
	{
		Instance = null;
	}

	// ── Public API ────────────────────────────────────────────────────────────

	/// <summary>
	/// Makes the panel visible and slides it out of the wall toward the player.
	/// Registers callbacks that fire when the player picks an option.
	/// </summary>
	public void Show(Action onLogin, Action onRegister)
	{
		_onLoginChosen    = onLogin;
		_onRegisterChosen = onRegister;

		Visible = true;

		// Start deep in the wall (positive local Z = into the wall surface)
		Position = _storedPosition + new Vector3(0, 0, 0.35f);

		CreateTween()
			.TweenProperty(this, "position", _storedPosition, 0.45f)
			.SetTrans(Tween.TransitionType.Cubic)
			.SetEase(Tween.EaseType.Out);

		_flickerAudio?.Play();
	}

	/// <summary>
	/// Slides the panel back into the wall and then hides it.
	/// Also resets hover state so no stale highlight lingers.
	/// </summary>
	public new void Hide()
	{
		_loginHovered    = false;
		_registerHovered = false;

		Vector3 hiddenPos = _storedPosition + new Vector3(0, 0, 0.35f);
		Tween t = CreateTween();
		t.TweenProperty(this, "position", hiddenPos, 0.3f)
			.SetTrans(Tween.TransitionType.Cubic)
			.SetEase(Tween.EaseType.In);
		t.Finished += () => Visible = false;
	}

	// ── Button handlers ───────────────────────────────────────────────────────

	private void OnLoginPressed()
	{
		Hide();
		_onLoginChosen?.Invoke();
	}

	private void OnRegisterPressed()
	{
		Hide();
		_onRegisterChosen?.Invoke();
	}

	// ── Flicker effect ────────────────────────────────────────────────────────
	// Runs always (even when hidden — cheap). Per-button hover state drives colour.

	private async void StartFlicker()
	{
		Vector3 loginBase    = _loginLabel?.Position    ?? Vector3.Zero;
		Vector3 registerBase = _registerLabel?.Position ?? Vector3.Zero;

		while (IsInsideTree())
		{
			// 12% chance of a brief blackout on non-hovered labels
			if (_rng.NextDouble() < 0.12)
			{
				if (!_loginHovered    && _loginLabel    != null) _loginLabel.Modulate    = Colors.Transparent;
				if (!_registerHovered && _registerLabel != null) _registerLabel.Modulate = Colors.Transparent;
				await ToSignal(GetTree().CreateTimer(0.025f + (float)_rng.NextDouble() * 0.04f),
					SceneTreeTimer.SignalName.Timeout);
			}

			SetLabelColor(_loginLabel,    _loginHovered);
			SetLabelColor(_registerLabel, _registerHovered);

			ApplyJitter(_loginLabel,    loginBase,    _loginHovered);
			ApplyJitter(_registerLabel, registerBase, _registerHovered);

			float wait = 0.04f + (float)_rng.NextDouble() * 0.13f;
			await ToSignal(GetTree().CreateTimer(wait), SceneTreeTimer.SignalName.Timeout);
		}
	}

	/// <summary>
	/// Hovered: bright white flicker with occasional pure-white flash.
	/// Normal:  hellfire orange-red.
	/// </summary>
	private void SetLabelColor(Label3D label, bool hovered)
	{
		if (label == null) return;

		if (hovered)
		{
			// Flicker between near-white and bright white
			float w = 0.80f + (float)_rng.NextDouble() * 0.20f;
			label.Modulate = new Color(w, w, w, 1f);
		}
		else
		{
			float brightness = 0.45f + (float)_rng.NextDouble() * 0.55f;
			label.Modulate = new Color(
				brightness,
				brightness * (0.15f + (float)_rng.NextDouble() * 0.25f),
				brightness * (float)(_rng.NextDouble() * 0.05f)
			);
		}
	}

	/// <summary>
	/// Nudges the label randomly. When hovered, trembles every tick with stronger
	/// displacement; otherwise 25% chance of a small neon-sign vibration.
	/// </summary>
	private void ApplyJitter(Label3D label, Vector3 basePos, bool hovered)
	{
		if (label == null) return;

		if (hovered)
		{
			float jx = (float)(_rng.NextDouble() * 0.006 - 0.003);
			float jy = (float)(_rng.NextDouble() * 0.004 - 0.002);
			label.Position = basePos + new Vector3(jx, jy, 0);
		}
		else
		{
			if (_rng.NextDouble() > 0.25) return;
			float jx = (float)(_rng.NextDouble() * 0.003 - 0.0015);
			float jy = (float)(_rng.NextDouble() * 0.001 - 0.0005);
			label.Position = basePos + new Vector3(jx, jy, 0);
		}
	}
}
