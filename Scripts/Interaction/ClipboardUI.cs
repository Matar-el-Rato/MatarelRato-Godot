using Godot;
using System;

public partial class ClipboardUI : Control
{
	[Export] public LineEdit UsernameInput;
	[Export] public LineEdit PasswordInput;
	[Export] public Button SignButton;
	[Export] public AudioStreamPlayer ScribbleAudio;

	private Random _random = new Random();
	private OmniLight3D _ambientFireLight;
	private Tween _firePulseTween;

	// Static storage for signed data (accessible from other scripts)
	public static System.Collections.Generic.Dictionary<string, string> SignedData = new();

	public override void _Ready()
	{
		UsernameInput ??= GetNode<LineEdit>("Field1/LineEdit");
		PasswordInput ??= GetNode<LineEdit>("Field2/LineEdit");
		SignButton ??= GetNode<Button>("SignButton");
		ScribbleAudio ??= GetNode<AudioStreamPlayer>("ScribbleAudio");

		if (SignButton != null)
		{
			SignButton.Pressed += OnSignButtonPressed;
		}

		if (UsernameInput != null)
		{
			UsernameInput.TextChanged += (text) => PlayScribble();
			SetupHover(UsernameInput);
		}
		if (PasswordInput != null)
		{
			PasswordInput.TextChanged += (text) => PlayScribble();
			SetupHover(PasswordInput);
		}
	}

	private StyleBoxFlat _normalStyle;
	private StyleBoxFlat _hoverStyle;

	private void SetupHover(LineEdit field)
	{
		// Cache the styles from the first field
		if (_normalStyle == null)
		{
			_normalStyle = field.GetThemeStylebox("normal") as StyleBoxFlat;
		}
		if (_hoverStyle == null)
		{
			// Try to find the hover style from the scene's sub-resources
			// We create it in code as fallback
			_hoverStyle = _normalStyle?.Duplicate() as StyleBoxFlat;
			if (_hoverStyle != null)
			{
				_hoverStyle.BgColor = new Color(1f, 0.95f, 0.9f, 0.08f);
				_hoverStyle.BorderColor = new Color(0.6f, 0.2f, 0.1f, 0.35f);
			}
		}

		field.MouseEntered += () =>
		{
			if (_hoverStyle != null && !field.HasFocus())
				field.AddThemeStyleboxOverride("normal", _hoverStyle);
		};
		field.MouseExited += () =>
		{
			if (_normalStyle != null)
				field.AddThemeStyleboxOverride("normal", _normalStyle);
		};
	}

	private void PlayScribble()
	{
		if (ScribbleAudio != null)
		{
			// Randomize volume between -10 and 0 (assuming 0 is max source volume)
			float vol = (float)(_random.NextDouble() * -10.0);
			ScribbleAudio.VolumeDb = vol;
			// Slight pitch variation for natural feel
			ScribbleAudio.PitchScale = (float)(0.9 + _random.NextDouble() * 0.2);
			
			if (!ScribbleAudio.Playing) ScribbleAudio.Play();
		}
	}

	/// <summary>
	/// Call from FocusController or FocusInteractable when this clipboard gains focus.
	/// Creates a pulsing ambient fire light near the clipboard.
	/// </summary>
	public void StartAmbientFire()
	{
		if (_ambientFireLight != null) return;

		// Find the parent Node3D (the clipboard scene root)
		Node3D clipboardRoot = null;
		Node current = this;
		while (current != null)
		{
			if (current is Node3D n3d && current.GetParent() is not SubViewport)
			{
				clipboardRoot = n3d;
				break;
			}
			current = current.GetParent();
		}

		if (clipboardRoot == null) return;

		_ambientFireLight = new OmniLight3D();
		_ambientFireLight.LightColor = new Color(1.0f, 0.4f, 0.1f);
		_ambientFireLight.LightEnergy = 0.0f;
		_ambientFireLight.OmniRange = 3.0f;
		_ambientFireLight.ShadowEnabled = false;
		clipboardRoot.GetParent().AddChild(_ambientFireLight);
		_ambientFireLight.GlobalPosition = clipboardRoot.GlobalPosition + new Vector3(0, 0.1f, 0);

		// Fade in
		Tween fadeIn = CreateTween();
		fadeIn.TweenProperty(_ambientFireLight, "light_energy", 0.8f, 0.4f);
		fadeIn.Finished += () => StartFirePulse();
	}

	private void StartFirePulse()
	{
		if (_ambientFireLight == null || !IsInstanceValid(_ambientFireLight)) return;

		_firePulseTween = CreateTween();
		_firePulseTween.SetLoops();
		// Irregular flickering pulse
		_firePulseTween.TweenProperty(_ambientFireLight, "light_energy", 1.2f, 0.3f)
			.SetTrans(Tween.TransitionType.Sine);
		_firePulseTween.TweenProperty(_ambientFireLight, "light_energy", 0.5f, 0.2f)
			.SetTrans(Tween.TransitionType.Sine);
		_firePulseTween.TweenProperty(_ambientFireLight, "light_energy", 0.9f, 0.15f)
			.SetTrans(Tween.TransitionType.Sine);
		_firePulseTween.TweenProperty(_ambientFireLight, "light_energy", 0.6f, 0.35f)
			.SetTrans(Tween.TransitionType.Sine);
	}

	/// <summary>
	/// Call when the clipboard loses focus or signs out. Fades and removes the fire light.
	/// </summary>
	public void StopAmbientFire()
	{
		if (_firePulseTween != null && _firePulseTween.IsValid())
		{
			_firePulseTween.Kill();
			_firePulseTween = null;
		}

		if (_ambientFireLight != null && IsInstanceValid(_ambientFireLight))
		{
			Tween fadeOut = CreateTween();
			var light = _ambientFireLight;
			_ambientFireLight = null;
			fadeOut.TweenProperty(light, "light_energy", 0.0f, 0.3f);
			fadeOut.Finished += () => { if (IsInstanceValid(light)) light.QueueFree(); };
		}
	}

	private void OnSignButtonPressed()
	{
		string user = UsernameInput?.Text ?? "";
		string pass = PasswordInput?.Text ?? "";

		// Play sound on sign too
		PlayScribble();

		// Store data for future auth
		SignedData["username"] = user;
		SignedData["password"] = pass;

		// Kill ambient fire before burn-out
		StopAmbientFire();
		
		// Trigger the burn-out effect and hide clipboards
		if (ClipboardController.Instance != null)
		{
			ClipboardController.Instance.HideClipboards();
		}

		// Exit focus (returns camera to player and unlocks movement)
		if (FocusController.Instance != null)
		{
			FocusController.Instance.ExitFocus();
		}
	}
}
