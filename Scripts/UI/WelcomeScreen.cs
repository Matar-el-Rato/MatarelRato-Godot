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
		// Viewport resolution for pixelated look
		GetWindow().ContentScaleMode = Window.ContentScaleModeEnum.Viewport;
		
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

		// Disable player movement in the background scene
		var player = GetNodeOrNull<PlayerCameraController>("BackgroundParent/BackgroundViewport/SubViewport/MainSceneInstance/Player");
		if (player != null)
		{
			player.MovementEnabled = false;
		}
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
		// Viewport resolution for pixelated gameplay and UI
		GetWindow().ContentScaleMode = Window.ContentScaleModeEnum.Viewport;
		
		var mainScene = GetNodeOrNull<Node3D>("BackgroundParent/BackgroundViewport/SubViewport/MainSceneInstance");
		if (mainScene != null)
		{
			// Reparent to root so it persists when WelcomeScreen is freed
			mainScene.GetParent().RemoveChild(mainScene);
			GetTree().Root.AddChild(mainScene);
			GetTree().CurrentScene = mainScene;

			// Re-enable player movement
			var player = mainScene.GetNodeOrNull<PlayerCameraController>("Player");
			if (player != null)
			{
				player.MovementEnabled = true;
				// Capture mouse for gameplay
				Input.MouseMode = Input.MouseModeEnum.Captured;
			}
		}

		QueueFree();
	}
}
