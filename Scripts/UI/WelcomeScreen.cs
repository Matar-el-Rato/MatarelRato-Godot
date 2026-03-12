using Godot;
using System;

public partial class WelcomeScreen : Control
{
	[Export] public string MainScenePath = "res://Scenes/MainScene.tscn";

	private Button _playButton;
	private Button _sourceCodeButton;
	private Button _debugButton;

	public override void _Ready()
	{
		// Viewport resolution for pixelated look
		GetWindow().ContentScaleMode = Window.ContentScaleModeEnum.Viewport;
		
		Input.MouseMode = Input.MouseModeEnum.Visible;
		_playButton = GetNode<Button>("MarginContainer/HBoxContainer/LeftPanel/VBoxContainer/PlayButton");
		_sourceCodeButton = GetNode<Button>("MarginContainer/HBoxContainer/LeftPanel/VBoxContainer/SourceCodeButton");
		_debugButton = GetNode<Button>("DebugButton");

		_playButton.Pressed += OnPlayPressed;
		_sourceCodeButton.Pressed += OnSourceCodePressed;
		_debugButton.Pressed += OnDebugPressed;

		_playButton.MouseEntered += () => OnHoverStarted(_playButton);
		_playButton.MouseExited += () => OnHoverEnded(_playButton);
		_sourceCodeButton.MouseEntered += () => OnHoverStarted(_sourceCodeButton);
		_sourceCodeButton.MouseExited += () => OnHoverEnded(_sourceCodeButton);

		// Disable player movement in the background scene
		var player = GetNodeOrNull<PlayerCameraController>("BackgroundParent/BackgroundViewport/SubViewport/MainSceneInstance/Player");
		if (player != null)
		{
			player.MovementEnabled = false;
		}
	}

	private void OnPlayPressed()
	{
		GD.Print("Play pressed - Initializing game flow");
		ProceedToMainScene();
	}

	private void OnSourceCodePressed()
	{
		GD.Print("Opening Source Code URL");
		OS.ShellOpen("https://github.com/Matar-el-Rato/MatarelRato-Godot");
	}

	private void OnDebugPressed()
	{
		GD.Print("Debug pressed - Proceeding to MainScene (Full Dev Mode)");
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
