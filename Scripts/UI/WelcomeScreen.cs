// ═══════════════════════════════════════════════════
// WelcomeScreen.cs
// Title-screen Control: shows Play / Source Code / Debug buttons
// over a live SubViewport preview of the main scene.
// On play, reparents the already-loaded scene to the tree root.
// ═══════════════════════════════════════════════════
using Godot;
using System;

/// <summary>
/// Welcome / title screen displayed at launch.
/// The main scene is pre-loaded in a background SubViewport;
/// pressing Play reparents it to the scene tree root and frees this screen.
/// </summary>
public partial class WelcomeScreen : Control
{
	[Export] public string MainScenePath = "res://Scenes/MainScene.tscn";

	private Button _playButton;
	private Button _sourceCodeButton;
	private Button _debugButton;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		// Force pixelated scaling mode for the title screen.
		GetWindow().ContentScaleMode = Window.ContentScaleModeEnum.Viewport;

		Input.MouseMode = Input.MouseModeEnum.Visible;

		_playButton       = GetNode<Button>("MarginContainer/HBoxContainer/LeftPanel/VBoxContainer/PlayButton");
		_sourceCodeButton = GetNode<Button>("MarginContainer/HBoxContainer/LeftPanel/VBoxContainer/SourceCodeButton");
		_debugButton      = GetNode<Button>("DebugButton");

		_playButton.Pressed       += OnPlayPressed;
		_sourceCodeButton.Pressed += OnSourceCodePressed;
		_debugButton.Pressed      += OnDebugPressed;

		_playButton.MouseEntered       += () => OnHoverStarted(_playButton);
		_playButton.MouseExited        += () => OnHoverEnded(_playButton);
		_sourceCodeButton.MouseEntered += () => OnHoverStarted(_sourceCodeButton);
		_sourceCodeButton.MouseExited  += () => OnHoverEnded(_sourceCodeButton);

		// Freeze the background preview; movement is re-enabled when Play is pressed.
		var player = GetNodeOrNull<PlayerCameraController>(
			"BackgroundParent/BackgroundViewport/SubViewport/MainSceneInstance/Player");
		if (player != null)
			player.MovementEnabled = false;
	}

	// ── Button handlers ───────────────────────────────────────────────────────

	private void OnPlayPressed()
	{
		ProceedToMainScene();
	}

	private void OnSourceCodePressed()
	{
		OS.ShellOpen("https://github.com/Matar-el-Rato/MatarelRato-Godot");
	}

	private void OnDebugPressed()
	{
		ProceedToMainScene();
	}

	// ── Hover effects ─────────────────────────────────────────────────────────

	private void OnHoverStarted(Button button)
	{
		// Brighten to pure white and scale up slightly for a pop effect.
		button.AddThemeColorOverride("font_color",       new Color(1, 1, 1));
		button.AddThemeColorOverride("font_hover_color", new Color(1, 1, 1));
		button.Scale = new Vector2(1.05f, 1.05f);
	}

	private void OnHoverEnded(Button button)
	{
		button.RemoveThemeColorOverride("font_color");
		button.Scale = new Vector2(1, 1);
	}

	// ── Scene transition ──────────────────────────────────────────────────────

	/// <summary>
	/// Moves the pre-loaded main scene from the background SubViewport to the scene
	/// tree root, sets it as the current scene, re-enables the player, and frees
	/// this welcome screen.
	/// </summary>
	private void ProceedToMainScene()
	{
		GetWindow().ContentScaleMode = Window.ContentScaleModeEnum.Viewport;

		var mainScene = GetNodeOrNull<Node3D>(
			"BackgroundParent/BackgroundViewport/SubViewport/MainSceneInstance");

		if (mainScene != null)
		{
			// Reparent preserves the scene state (physics, audio, etc.).
			mainScene.GetParent().RemoveChild(mainScene);
			GetTree().Root.AddChild(mainScene);
			GetTree().CurrentScene = mainScene;

			var player = mainScene.GetNodeOrNull<PlayerCameraController>("Player");
			if (player != null)
			{
				player.MovementEnabled = true;
				Input.MouseMode        = Input.MouseModeEnum.Captured;
			}
		}

		QueueFree();
	}
}
