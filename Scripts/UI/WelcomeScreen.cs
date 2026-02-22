using Godot;
using System;

public partial class WelcomeScreen : Control
{
	[Export] public string MainScenePath = "res://Scenes/MainScene.tscn";

	private Button _registerButton;
	private Button _loginButton;
	private Button _debugButton;

	public override void _Ready()
	{
		// Native resolution for UI clarity on welcome screen
		GetWindow().ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
		
		Input.MouseMode = Input.MouseModeEnum.Visible;
		_registerButton = GetNode<Button>("MarginContainer/HBoxContainer/LeftPanel/VBoxContainer/RegisterButton");
		_loginButton = GetNode<Button>("MarginContainer/HBoxContainer/LeftPanel/VBoxContainer/LoginButton");
		_debugButton = GetNode<Button>("DebugButton");

		_registerButton.Pressed += OnRegisterPressed;
		_loginButton.Pressed += OnLoginPressed;
		_debugButton.Pressed += OnDebugPressed;

		_registerButton.MouseEntered += () => OnHoverStarted(_registerButton);
		_registerButton.MouseExited += () => OnHoverEnded(_registerButton);
		_loginButton.MouseEntered += () => OnHoverStarted(_loginButton);
		_loginButton.MouseExited += () => OnHoverEnded(_loginButton);
	}

	private void OnRegisterPressed()
	{
		GD.Print("Register pressed - Logic not yet implemented");
	}

	private void OnLoginPressed()
	{
		GD.Print("Login pressed - Logic not yet implemented");
	}

	private void OnDebugPressed()
	{
		GD.Print("Debug pressed - Proceeding to MainScene");
		ProceedToMainScene();
	}

	private void OnHoverStarted(Button button)
	{
		// Contrast highlight effect: brighten the text significantly
		button.AddThemeColorOverride("font_color", new Color(1, 1, 1)); // Pure white
		button.AddThemeColorOverride("font_hover_color", new Color(1, 1, 1));
		button.Scale = new Vector2(1.05f, 1.05f); // Subtle pop
	}

	private void OnHoverEnded(Button button)
	{
		// Restore original dim color
		button.RemoveThemeColorOverride("font_color");
		button.Scale = new Vector2(1, 1);
	}

	private void ProceedToMainScene()
	{
		// Back to pixelated look for the actual game
		GetWindow().ContentScaleMode = Window.ContentScaleModeEnum.Viewport;
		GetTree().ChangeSceneToFile(MainScenePath);
	}
}
