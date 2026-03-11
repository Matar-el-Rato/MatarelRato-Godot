using Godot;
using System;

public partial class ClipboardUI : Control
{
	[Export] public LineEdit UsernameInput;
	[Export] public LineEdit PasswordInput;
	[Export] public Button SignButton;
	[Export] public AudioStreamPlayer ScribbleAudio;

	private Random _random = new Random();

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

		if (UsernameInput != null) UsernameInput.TextChanged += (text) => PlayScribble();
		if (PasswordInput != null) PasswordInput.TextChanged += (text) => PlayScribble();
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

	private void OnSignButtonPressed()
	{
		string user = UsernameInput?.Text ?? "";
		string pass = PasswordInput?.Text ?? "";
		
		GD.Print($"[ClipboardUI] Signed by: {user}");
		
		// Play sound on sign too
		PlayScribble();

		// Store data for future auth
		SignedData["username"] = user;
		SignedData["password"] = pass;
		
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
