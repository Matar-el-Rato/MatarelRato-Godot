// ═══════════════════════════════════════════════════
// WelcomeScreen.cs
// WelcomeScreen3D plays in the background on the title screen.
// On Play: crossfades to the pre-loaded MainScene (blurred, looking down),
// sweeps the camera up to eye level, unblurs, then hands off to gameplay.
// ═══════════════════════════════════════════════════
using Godot;

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

	private bool _debugMode         = false;
	private bool _transitionStarted = false;

	// Intro sweep
	private Camera3D       _playerCamera;
	private ShaderMaterial _blurMaterial;
	private const float    TransitionDuration = 3.0f;

	private LightFlicker _intenseFlicker;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		GetWindow().ContentScaleMode = Window.ContentScaleModeEnum.Viewport;
		Input.MouseMode = Input.MouseModeEnum.Visible;

		_playButton       = GetNode<Button>("VBoxContainer/PlayButton");
		_sourceCodeButton = GetNode<Button>("VBoxContainer/SourceCodeButton");
		_debugButton      = GetNode<Button>("DebugButton");

		_playButton.Pressed       += OnPlayPressed;
		_sourceCodeButton.Pressed += OnSourceCodePressed;
		_debugButton.Pressed      += OnDebugPressed;

		_playButton.MouseEntered       += () => OnHoverStarted(_playButton);
		_playButton.MouseExited        += () => OnHoverEnded(_playButton);
		_sourceCodeButton.MouseEntered += () => OnHoverStarted(_sourceCodeButton);
		_sourceCodeButton.MouseExited  += () => OnHoverEnded(_sourceCodeButton);

		// Cache blur material.
		var blurOverlay = GetNodeOrNull<ColorRect>("GameBackground/BlurOverlay");
		if (blurOverlay != null)
			_blurMaterial = blurOverlay.Material as ShaderMaterial;

		_intenseFlicker = GetNodeOrNull<LightFlicker>(
			"WelcomeBackground/WelcomeViewport/SubViewport/WelcomeScene/scene_render/flicker_lights_intense");

		// Callable.From bypasses Godot reflection so the private method actually fires.
		Callable.From(SetupIntroCamera).CallDeferred();
	}

	// ── Intro camera ──────────────────────────────────────────────────────────

	private void SetupIntroCamera()
	{
		// Freeze the player now that the SubViewport tree is fully ready.
		var player = GetNodeOrNull<PlayerCameraController>(
			"GameBackground/GameViewport/SubViewport/MainSceneInstance/Player");
		if (player != null)
			player.MovementEnabled = false;

		_playerCamera = GetNodeOrNull<Camera3D>(
			"GameBackground/GameViewport/SubViewport/MainSceneInstance/Player/Camera3D");

		if (_playerCamera == null)
		{
			GD.PushWarning("[WelcomeScreen] Could not find player camera — intro sweep disabled.");
			return;
		}

		// Ensure SubViewport renders from this camera immediately.
		_playerCamera.MakeCurrent();

		// Pre-tilt the camera to look steeply downward. On Play it sweeps back to forward.
		// PlayerCameraController only updates rotation via _UnhandledInput (guarded by
		// MovementEnabled), so this won't be overridden while the player is frozen.
		_playerCamera.Rotation = new Vector3(Mathf.DegToRad(-35f), 0f, 0f);
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
		_intenseFlicker?.SetIntenseMode(true);
	}

	private void OnHoverEnded(Button button)
	{
		_hoveredButton = null;
		button.Position = _hoveredOrigin;
		button.RemoveThemeColorOverride("font_color");
		button.Scale = new Vector2(1, 1);
		_intenseFlicker?.SetIntenseMode(false);
	}

	// ── Button handlers ───────────────────────────────────────────────────────

	private void OnPlayPressed()  => StartIntroTransition();
	private void OnDebugPressed() { _debugMode = true; StartIntroTransition(); }

	private void OnSourceCodePressed()
	{
		OS.ShellOpen("https://github.com/Matar-el-Rato/MatarelRato-Godot");
	}

	// ── Transition ────────────────────────────────────────────────────────────

	private void StartIntroTransition()
	{
		if (_transitionStarted) return;
		_transitionStarted = true;
		_hoveredButton     = null;

		_playButton.Disabled       = true;
		_debugButton.Disabled      = true;
		_sourceCodeButton.Disabled = true;

		var welcomeBg = GetNodeOrNull<Control>("WelcomeBackground");
		var gameBg    = GetNodeOrNull<Control>("GameBackground");

		const float FadeOut  = 1.0f;   // welcome → black
		const float FadeIn   = 0.7f;   // black → blurry game world

		var mainTween = CreateTween().SetParallel(true);

		// Phase 1: fade welcome scene + UI to black.
		if (welcomeBg != null)
		{
			mainTween.TweenProperty(welcomeBg, "modulate:a", 0f, FadeOut)
					 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
			mainTween.TweenCallback(Callable.From(welcomeBg.QueueFree)).SetDelay(FadeOut);
		}
		var uiContainer = GetNodeOrNull<Control>("MarginContainer");
		if (uiContainer != null)
			mainTween.TweenProperty(uiContainer, "modulate:a", 0f, FadeOut)
					 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
		var vbox = GetNodeOrNull<Control>("VBoxContainer");
		if (vbox != null)
			mainTween.TweenProperty(vbox, "modulate:a", 0f, FadeOut)
					 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
		mainTween.TweenProperty(_debugButton, "modulate:a", 0f, FadeOut)
				 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);

		// Phase 2: fade game world in (delayed so black frame is visible).
		if (gameBg != null)
			mainTween.TweenProperty(gameBg, "modulate:a", 1f, FadeIn)
					 .SetDelay(FadeOut)
					 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);

		// Phase 3: camera sweep + unblur start when game world begins appearing.
		if (_playerCamera != null)
		{
			mainTween.TweenProperty(_playerCamera, "rotation", Vector3.Zero, TransitionDuration)
					 .SetDelay(FadeOut)
					 .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);

			mainTween.Finished += () => ProceedToMainScene();

			if (_blurMaterial != null)
			{
				var blurTween = CreateTween();
				blurTween.TweenInterval(FadeOut);
				blurTween.TweenMethod(
					Callable.From<float>(v => _blurMaterial.SetShaderParameter("lod", v)),
					1.5f, 0.0f, TransitionDuration)
					.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
			}
		}
		else
		{
			mainTween.Finished += () => ProceedToMainScene();
		}
	}

	// ── Hand off to gameplay ──────────────────────────────────────────────────

	private void ProceedToMainScene()
	{
		GetWindow().ContentScaleMode = Window.ContentScaleModeEnum.Viewport;

		var mainScene = GetNodeOrNull<Node3D>(
			"GameBackground/GameViewport/SubViewport/MainSceneInstance");

		if (mainScene != null)
		{
			mainScene.GetParent().RemoveChild(mainScene);
			GetTree().Root.AddChild(mainScene);
			GetTree().CurrentScene = mainScene;

			// Re-activate the camera in its new viewport so there's no black frame.
			_playerCamera?.MakeCurrent();

			var player = mainScene.GetNodeOrNull<PlayerCameraController>("Player");
			if (player != null)
			{
				player.MovementEnabled = true;
				Input.MouseMode        = Input.MouseModeEnum.Captured;
			}
		}

		// Zero out GameBackground before QueueFree: the SubViewport is now empty
		// (MainScene was reparented) so it would flash black for one frame otherwise.
		GetNodeOrNull<Control>("GameBackground")?.Hide();

		if (_debugMode)
		{
			var tree = GetTree();
			tree.CreateTimer(0.3f).Timeout += async () =>
			{
				var result = await System.Threading.Tasks.Task.Run(() =>
					ServerProtocol.LoginUser(
						ServerProtocol.DefaultHost,
						ServerProtocol.DefaultPort,
						"admin-godot", "godot"));

				if (result.IsSuccess)
				{
					AuthManager.NotifySuccess("admin-godot", result.UserId, result.SkinId, false);
					ChatManager.AddLog("[color=#888888][debug] logged in as admin-godot[/color]");
					ApplySkin(result.SkinId, tree);
				}
			};
		}

		QueueFree();
	}

	private static void ApplySkin(int skinId, SceneTree tree)
	{
		var player   = tree.Root.FindChild("Player", true, false) as PlayerCameraController;
		var selector = player?.GetNodeOrNull<Selector>("Selector");
		if (player == null || selector?.Entries == null) return;

		foreach (var entry in selector.Entries)
		{
			if (entry?.ServerId == skinId)
			{
				player.SwapCharacter(entry);
				if (tree.Root.FindChild("CharacterSelector", true, false) is CharacterSelector cs)
					cs.SnapToSkin(skinId);
				return;
			}
		}
	}
}
