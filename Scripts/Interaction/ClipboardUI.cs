using Godot;
using System;

public partial class ClipboardUI : Control
{
	[Export] public LineEdit UsernameInput;
	[Export] public LineEdit PasswordInput;
	[Export] public Button SignButton;

	public override void _Ready()
	{
		UsernameInput ??= GetNode<LineEdit>("Margin/VBox/Field1/LineEdit");
		PasswordInput ??= GetNode<LineEdit>("Margin/VBox/Field2/LineEdit");
		SignButton ??= GetNode<Button>("Margin/VBox/SignButton");

		if (SignButton != null)
		{
			SignButton.Pressed += OnSignButtonPressed;
		}
	}

	private void OnSignButtonPressed()
	{
		GD.Print($"[ClipboardUI] Signed by: {UsernameInput?.Text}, Password length: {PasswordInput?.Text.Length}");
		// For now just exit focus
		if (FocusController.Instance != null)
		{
			FocusController.Instance.ExitFocus();
		}
	}
}
