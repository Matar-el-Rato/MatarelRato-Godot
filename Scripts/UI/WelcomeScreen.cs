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
	private Button _closeButton;

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

		// The pause menu / Tab hint is dormant on the title screen; it fades in once
		// we hand off to gameplay (see ProceedToMainScene).
		GetNodeOrNull<PauseMenu>("/root/PauseMenu")?.Deactivate();

		_playButton       = GetNode<Button>("VBoxContainer/PlayButton");
		_sourceCodeButton = GetNode<Button>("VBoxContainer/SourceCodeButton");
		_debugButton      = GetNode<Button>("DebugButton");
		_closeButton      = GetNode<Button>("CloseButton");

		_playButton.Pressed       += OnPlayPressed;
		_sourceCodeButton.Pressed += OnSourceCodePressed;
		_debugButton.Pressed      += OnDebugPressed;
		_closeButton.Pressed      += OnClosePressed;

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

	private void OnClosePressed() => GetTree().Quit();

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

		const float MeshFadeDuration  = 1.8f;
		const float HoldDuration      = 0.5f;
		const float SwordFadeDuration = 0.8f;
		const float FadeOut           = MeshFadeDuration + HoldDuration + SwordFadeDuration; // 3.1s total
		const float FadeIn            = 0.7f;   // black → blurry game world

		var mainTween = CreateTween().SetParallel(true);

		// Phase 1: Fade out UI to black immediately (over 0.6s).
		var uiContainer = GetNodeOrNull<Control>("MarginContainer");
		if (uiContainer != null)
			mainTween.TweenProperty(uiContainer, "modulate:a", 0f, 0.6f)
					 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
		var vbox = GetNodeOrNull<Control>("VBoxContainer");
		if (vbox != null)
			mainTween.TweenProperty(vbox, "modulate:a", 0f, 0.6f)
					 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
		mainTween.TweenProperty(_debugButton, "modulate:a", 0f, 0.6f)
				 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);

		// Phase 1b: Fade out WelcomeScene meshes EXCEPT the sword to pure black (over 1.8s).
		var sceneRender = GetNodeOrNull("WelcomeBackground/WelcomeViewport/SubViewport/WelcomeScene/scene_render");
		if (sceneRender != null)
		{
			var swordNode = FindSwordNode(sceneRender);
			if (swordNode != null)
			{
				GD.Print($"[WelcomeScreen] Found sword node for transition exemption: {swordNode.Name}");
			}
			FadeToBlackNodeRecursive(sceneRender, mainTween, MeshFadeDuration, swordNode);
		}

		// Phase 1c: Fade the remaining sword (by fading the whole WelcomeBackground container) after 2.3s (1.8s fade + 0.5s hold).
		if (welcomeBg != null)
		{
			mainTween.TweenProperty(welcomeBg, "modulate:a", 0f, SwordFadeDuration)
					 .SetDelay(MeshFadeDuration + HoldDuration)
					 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
			mainTween.TweenCallback(Callable.From(welcomeBg.QueueFree)).SetDelay(FadeOut);
		}

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

	private Node3D FindSwordNode(Node node)
	{
		if (node == null) return null;
		string name = node.Name.ToString().ToLower();
		if (node is Node3D node3D && (name.Contains("sword") || name.Contains("damocles")))
		{
			return node3D;
		}
		foreach (Node child in node.GetChildren())
		{
			var found = FindSwordNode(child);
			if (found != null) return found;
		}
		return null;
	}

	private bool IsAncestorOf(Node parent, Node child)
	{
		if (parent == null || child == null) return false;
		var p = child.GetParent();
		while (p != null)
		{
			if (p == parent) return true;
			p = p.GetParent();
		}
		return false;
	}

	private void FadeToBlackNodeRecursive(Node node, Tween tween, float duration, Node exceptionNode)
	{
		if (node == null || node == exceptionNode) return;

		if (IsAncestorOf(node, exceptionNode))
		{
			foreach (Node child in node.GetChildren())
			{
				FadeToBlackNodeRecursive(child, tween, duration, exceptionNode);
			}
			return;
		}

		if (node is Light3D light)
		{
			if (node.Name != "SpotLight3D")
			{
				tween.TweenProperty(light, "light_energy", 0.0f, duration)
					 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
			}
		}
		else if (node is GpuParticles3D gpuParticles)
		{
			gpuParticles.Emitting = false;
		}
		else if (node is CpuParticles3D cpuParticles)
		{
			cpuParticles.Emitting = false;
		}
		else if (node is MeshInstance3D meshInstance)
		{
			for (int i = 0; i < meshInstance.GetSurfaceOverrideMaterialCount(); i++)
			{
				var mat = meshInstance.GetSurfaceOverrideMaterial(i) as StandardMaterial3D;
				if (mat == null)
				{
					var activeMat = meshInstance.GetActiveMaterial(i) as StandardMaterial3D;
					if (activeMat != null)
					{
						mat = (StandardMaterial3D)activeMat.Duplicate();
						meshInstance.SetSurfaceOverrideMaterial(i, mat);
					}
				}

				if (mat != null)
				{
					tween.TweenProperty(mat, "albedo_color", new Color(0f, 0f, 0f, 1f), duration)
						 .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
					tween.TweenProperty(mat, "metallic", 0.0f, duration)
						 .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
					tween.TweenProperty(mat, "roughness", 1.0f, duration)
						 .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
					if (mat.EmissionEnabled)
					{
						tween.TweenProperty(mat, "emission", new Color(0f, 0f, 0f, 1f), duration)
							 .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
					}
				}
			}
		}

		foreach (Node child in node.GetChildren())
		{
			FadeToBlackNodeRecursive(child, tween, duration, exceptionNode);
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

		// Now that we're in the game world, reveal the Tab pause hint (fades in).
		GetNodeOrNull<PauseMenu>("/root/PauseMenu")?.Activate();

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
