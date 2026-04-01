// ═══════════════════════════════════════════════════
// WelcomeScreen.cs
// Title-screen Control: shows Play / Source Code / Debug buttons
// over a live SubViewport preview of the main scene.
// On play, runs a 3-second intro camera sweep (floor → player head)
// with blur intensifying, then reparents the loaded scene to the tree root.
// ═══════════════════════════════════════════════════
using Godot;

/// <summary>
/// Welcome / title screen displayed at launch.
/// The main scene is pre-loaded in a background SubViewport.
/// Pressing Play triggers a loomy intro camera sequence before gameplay begins.
/// </summary>
public partial class WelcomeScreen : Control
{
	[Export] public string MainScenePath = "res://Scenes/MainScene.tscn";

	private Button _playButton;
	private Button _sourceCodeButton;
	private Button _debugButton;

	private Button _hoveredButton;
	private Vector2 _hoveredOrigin;
	private RandomNumberGenerator _rng = new();
	private const float TrembleStrength = 1.2f;

	// Intro camera transition
	private Camera3D       _introCamera;
	private Camera3D       _playerCamera;
	private ShaderMaterial _blurMaterial;
	private bool           _transitionStarted = false;
	private const float    TransitionDuration = 3.0f;

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

		// Freeze the background preview; movement re-enabled after the intro sequence.
		var player = GetNodeOrNull<PlayerCameraController>(
			"BackgroundParent/BackgroundViewport/SubViewport/MainSceneInstance/Player");
		if (player != null)
			player.MovementEnabled = false;

		// Cache the blur shader material.
		var blurOverlay = GetNodeOrNull<ColorRect>("BackgroundParent/BlurOverlay");
		if (blurOverlay != null)
			_blurMaterial = blurOverlay.Material as ShaderMaterial;

		// Set up the ground-level intro camera.
		SetupIntroCamera();
	}

	// ── Intro camera setup ────────────────────────────────────────────────────

	/// <summary>
	/// Injects a new Camera3D into the SubViewport positioned at floor level
	/// looking sharply upward, replacing the player camera as the active view.
	/// </summary>
	private void SetupIntroCamera()
	{
		_playerCamera = GetNodeOrNull<Camera3D>(
			"BackgroundParent/BackgroundViewport/SubViewport/MainSceneInstance/Player/Camera3D");

		var subViewport = GetNodeOrNull<SubViewport>(
			"BackgroundParent/BackgroundViewport/SubViewport");

		if (_playerCamera == null || subViewport == null) return;

		_introCamera     = new Camera3D();
		_introCamera.Fov = _playerCamera.Fov;
		subViewport.AddChild(_introCamera);

		// Start near the player's feet, rotated ~80° upward (almost looking at the sky).
		Vector3 camWorldPos = _playerCamera.GlobalPosition;
		_introCamera.GlobalPosition = new Vector3(camWorldPos.X, camWorldPos.Y - 1.5f, camWorldPos.Z);
		_introCamera.GlobalRotation = new Vector3(
			Mathf.DegToRad(-80f),
			_playerCamera.GlobalRotation.Y,
			0f);

		// Hand the viewport over to the intro camera.
		_introCamera.Current  = true;
		_playerCamera.Current = false;
	}

	// ── Button handlers ───────────────────────────────────────────────────────

	private void OnPlayPressed()   => StartIntroTransition();
	private void OnDebugPressed()  => StartIntroTransition();

	private void OnSourceCodePressed()
	{
		OS.ShellOpen("https://github.com/Matar-el-Rato/MatarelRato-Godot");
	}

	// ── Hover effects ─────────────────────────────────────────────────────────

	public override void _Process(double delta)
	{
		if (_hoveredButton != null)
		{
			float ox = _rng.RandfRange(-TrembleStrength, TrembleStrength);
			float oy = _rng.RandfRange(-TrembleStrength, TrembleStrength);
			_hoveredButton.Position = _hoveredOrigin + new Vector2(ox, oy);
		}
	}

	private void OnHoverStarted(Button button)
	{
		button.AddThemeColorOverride("font_color",       new Color(1, 1, 1));
		button.AddThemeColorOverride("font_hover_color", new Color(1, 1, 1));
		button.Scale = new Vector2(1.05f, 1.05f);
		_hoveredOrigin = button.Position;
		_hoveredButton = button;
	}

	private void OnHoverEnded(Button button)
	{
		_hoveredButton = null;
		button.Position = _hoveredOrigin;
		button.RemoveThemeColorOverride("font_color");
		button.Scale = new Vector2(1, 1);
	}

	// ── Intro transition ──────────────────────────────────────────────────────

	/// <summary>
	/// Disables UI input, then tweens the intro camera from the floor (looking up)
	/// to the player's eye position. The UI fades out, the blur swells then clears,
	/// so gameplay starts with a fully sharp view. Calls <see cref="ProceedToMainScene"/> when done.
	/// </summary>
	private void StartIntroTransition()
	{
		if (_transitionStarted) return;
		_transitionStarted = true;

		// Block all buttons for the duration.
		_playButton.Disabled       = true;
		_debugButton.Disabled      = true;
		_sourceCodeButton.Disabled = true;
		_hoveredButton             = null;

		if (_introCamera == null || _playerCamera == null)
		{
			// No cameras found — skip straight to gameplay.
			ProceedToMainScene();
			return;
		}

		Vector3 targetPos = _playerCamera.GlobalPosition;
		Vector3 targetRot = _playerCamera.GlobalRotation;

		// ── Camera sweep + UI fade (all parallel) ────────────────────────────
		var mainTween = CreateTween();
		mainTween.SetParallel(true);

		// Sweep the camera from the floor up to the player's eye.
		mainTween.TweenProperty(_introCamera, "global_position", targetPos, TransitionDuration)
				 .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
		mainTween.TweenProperty(_introCamera, "global_rotation", targetRot, TransitionDuration)
				 .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);

		// Fade out UI elements so they dissolve rather than snapping away.
		var uiContainer = GetNodeOrNull<Control>("MarginContainer");
		if (uiContainer != null)
			mainTween.TweenProperty(uiContainer, "modulate", new Color(1f, 1f, 1f, 0f), 1.0f)
					 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
		mainTween.TweenProperty(_debugButton, "modulate", new Color(1f, 1f, 1f, 0f), 1.0f)
				 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);

		mainTween.Finished += () => ProceedToMainScene();

		// ── Blur: swell up then fade back to clear (sequential, separate tween) ──
		// Runs in parallel with the camera sweep but drives its own timing.
		// Result: blur peaks at the midpoint and is already 0 when gameplay starts.
		if (_blurMaterial != null)
		{
			float half = TransitionDuration * 0.5f;
			var blurTween = CreateTween(); // sequential by default
			blurTween.TweenMethod(
				Callable.From<float>(v => _blurMaterial.SetShaderParameter("lod", v)),
				2.5f, 5.0f, half)
				.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
			blurTween.TweenMethod(
				Callable.From<float>(v => _blurMaterial.SetShaderParameter("lod", v)),
				5.0f, 0.0f, half)
				.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
		}
	}

	// ── Scene transition ──────────────────────────────────────────────────────

	/// <summary>
	/// Moves the pre-loaded main scene from the background SubViewport to the scene
	/// tree root, restores the player camera, re-enables the player, and frees
	/// this welcome screen.
	/// </summary>
	private void ProceedToMainScene()
	{
		GetWindow().ContentScaleMode = Window.ContentScaleModeEnum.Viewport;

		// Restore the player's own camera before we leave the SubViewport.
		if (_playerCamera != null)
			_playerCamera.Current = true;

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
