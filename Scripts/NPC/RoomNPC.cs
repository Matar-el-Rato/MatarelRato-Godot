// ═══════════════════════════════════════════════════
// RoomNPC.cs
// Standalone Pigga NPC for private rooms.
// Appears on player proximity, interactable for quirky dialogue.
// First interaction: welcome cutscene (camera lock, player turn, apparel open).
// Entirely independent of the auth system and the pizzeria NPC.
// ═══════════════════════════════════════════════════
using Godot;
using System;
using System.Collections.Generic;

public partial class RoomNPC : CharacterBody3D
{
	[Export] public string IdleAnimation      = "Armature|mixamo_com|Layer0_007";
	[Export] public string TalkAnimation      = "Armature_002|mixamo_com|Layer0_001";
	[Export] public float  TransitionDuration = 0.8f;
	[Export] public Color  TransitionColor    = new Color(1.0f, 0.5f, 0.2f);

	/// <summary>Player reference needed for the welcome camera sequence.</summary>
	[Export] public CharacterBody3D Player;
	/// <summary>Left apparel Node3D — slides out on welcome, resets on room exit.</summary>
	[Export] public Node3D LeftApparel;
	/// <summary>Right apparel Node3D — slides out on welcome, resets on room exit.</summary>
	[Export] public Node3D RightApparel;
	/// <summary>Node3D whose Light3D descendants shift to moody orange on welcome.</summary>
	[Export] public Node3D LightingNode;

	private static readonly string[] TalkPhrases =
	{
		"Wanna join?",
		"Take a seat.\nThe night's still young.",
		"What are ya waiting for?",
		"Got a good feeling about tonight...",
		"Come on in.\nDon't be shy.",
		"The table's right there.",
		"You look lucky.\n[i]Dangerously[/i] lucky.",
	};

	private static readonly string[] JoinedPhrases =
	{
		"Don't look at me.",
		"I'm just standing here.",
		"...",
		"You still here?",
		"Go play already.",
		"I don't do conversation.",
		"My feet hurt.",
		"Nice shoes.\nAnyway.",
		"The ceiling's interesting\nisn't it.",
		"I said what I said.",
	};

	private static readonly Color MoodyColor = new Color(1.0f, 0.82f, 0.70f);

	private AnimationPlayer     _animPlayer;
	private DialogBubble        _dialogBubble;
	private Interactable        _interactable;
	private AudioStreamPlayer3D _burnAudio;
	private AudioStreamPlayer3D _stoneScrapeAudio;
	private readonly Random     _rng = new Random();
	private readonly List<(Light3D light, Color originalColor)> _moodLights = new();

	private bool _hasAppeared   = false;
	private bool _hasGreeted    = false;
	private bool _hasInteracted = false;
	private bool _playerJoined  = false;

	public bool HasAppeared => _hasAppeared;

	/// <summary>Fired once when the first-entry welcome sequence fully finishes.</summary>
	public event Action WelcomeCompleted;
	/// <summary>Fired when the apparel panels finish sliding open.</summary>
	public event Action ApparelOpened;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_animPlayer   = GetNodeOrNull<AnimationPlayer>("OrientationFix/pigga/AnimationPlayer");
		if (_animPlayer == null) _animPlayer = FindAnimationPlayer(this);
		_burnAudio    = GetNodeOrNull<AudioStreamPlayer3D>("BurnAudio");

		var stoneScrapeStream = ResourceLoader.Load<AudioStream>("res://Assets/Sound FX/stone_scrape.wav");
		if (stoneScrapeStream != null)
		{
			_stoneScrapeAudio        = new AudioStreamPlayer3D();
			_stoneScrapeAudio.Stream = stoneScrapeStream;
			AddChild(_stoneScrapeAudio);
		}
		_dialogBubble = GetNodeOrNull<DialogBubble>("DialogBubble");
		_interactable = GetNodeOrNull<Interactable>("Interactable");

		if (_animPlayer != null && _animPlayer.HasAnimation(IdleAnimation))
		{
			var anim = _animPlayer.GetAnimation(IdleAnimation);
			anim.LoopMode = Animation.LoopModeEnum.Linear;
			_animPlayer.Play(IdleAnimation);
		}

		Visible = false;
		Scale   = new Vector3(0.001f, 0.001f, 0.001f);

		if (_interactable != null)
		{
			_interactable.PromptText  = "Talk";
			_interactable.Interacted += OnInteracted;
		}

		// Auto-find if not wired in editor
		if (Player == null)
			Player = GetTree().Root.FindChild("Player", true, false) as CharacterBody3D;
		if (LeftApparel == null)
			LeftApparel  = GetTree().Root.FindChild("left_apparel",  true, false) as Node3D;
		if (RightApparel == null)
			RightApparel = GetTree().Root.FindChild("right_apparel", true, false) as Node3D;
		if (LightingNode == null)
			LightingNode = GetParent()?.GetNodeOrNull<Node3D>("Lighting");

		if (LightingNode != null)
			CollectMoodLights(LightingNode);
	}

	private void CollectMoodLights(Node node)
	{
		if (node is Light3D light)
			_moodLights.Add((light, light.LightColor));
		foreach (Node child in node.GetChildren())
			CollectMoodLights(child);
	}

	// ── Appear / Disappear ────────────────────────────────────────────────────

	public void Appear()
	{
		if (_hasAppeared) return;
		_hasAppeared = true;

		Visible = true;
		Tween tween = CreateTween();
		tween.TweenProperty(this, "scale", Vector3.One, TransitionDuration)
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.InOut);

		AddBurnFlash();
		AddEmbers();
		_burnAudio?.Play();
	}

	public void Disappear()
	{
		if (!_hasAppeared) return;
		_hasAppeared   = false;
		_hasInteracted = false;

		_dialogBubble?.HideDialog();
		AddBurnFlash();
		AddEmbers();
		_burnAudio?.Play();

		Tween tween = CreateTween();
		tween.TweenProperty(this, "scale", new Vector3(0.001f, 0.001f, 0.001f), TransitionDuration)
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.InOut);
		tween.Finished += () => Visible = false;
	}

	/// <summary>Called by RoomJoin when the player officially joins or leaves the room.</summary>
	public void SetPlayerJoined(bool joined) => _playerJoined = joined;

	/// <summary>Resets the first-talk state and restores light colors for the next entry.</summary>
	public void ResetWelcome()
	{
		_hasGreeted    = false;
		_playerJoined  = false;
		foreach (var (light, originalColor) in _moodLights)
			if (IsInstanceValid(light))
			{
				var t = CreateTween();
				t.TweenProperty(light, "light_color", originalColor, 1.0f)
				 .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
			}
	}

	// ── Interaction ───────────────────────────────────────────────────────────

	private async void OnInteracted()
	{
		if (!_hasAppeared || _hasInteracted) return;
		_hasInteracted = true;

		if (!_hasGreeted)
		{
			_hasGreeted = true;
			await DoWelcomeSequence();
		}
		else
		{
			var pool   = _playerJoined ? JoinedPhrases : TalkPhrases;
			string phrase = pool[_rng.Next(pool.Length)];
			_dialogBubble?.ShowDialog(phrase, force: true);

			if (_animPlayer != null && _animPlayer.HasAnimation(TalkAnimation))
			{
				_animPlayer.Play(TalkAnimation, 0.3f);
				await ToSignal(_animPlayer, AnimationPlayer.SignalName.AnimationFinished);
				if (!IsInsideTree()) return;
				_animPlayer.Play(IdleAnimation, 0.3f);
			}

			_dialogBubble?.HideDialog();
		}

		_hasInteracted = false;
	}

	// ── Welcome sequence ──────────────────────────────────────────────────────

	private async System.Threading.Tasks.Task DoWelcomeSequence()
	{
		// 1. Show dialog and lock look.
		_dialogBubble?.ShowDialog("Welcome.\nThe room is yours.", force: true);
		var pcc    = Player as PlayerCameraController;
		var camera = Player?.GetNodeOrNull<Camera3D>("Camera3D");
		if (pcc != null) { pcc.MouseLookEnabled = false; pcc.MovementEnabled = false; }

		float originalRotY = Player?.Rotation.Y ?? 0f;
		float originalFov  = camera?.Fov ?? 75f;

		// 2. Hold 2 s on the dialog.
		await ToSignal(GetTree().CreateTimer(2.0f), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) return;

		// 3. Snap-turn to look behind and expand FOV slightly.
		{
			var turnTween = CreateTween();
			turnTween.SetParallel(true);
			if (Player != null)
				turnTween.TweenProperty(Player, "rotation:y", -Mathf.Pi / 2f, 0.35f)
						 .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
			if (camera != null)
				turnTween.TweenProperty(camera, "fov", originalFov + 14f, 0.35f)
						 .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
			await ToSignal(turnTween, Tween.SignalName.Finished);
			if (!IsInsideTree()) return;
		}

		// 4. Brief pause then slide apparel and shift mood lighting.
		await ToSignal(GetTree().CreateTimer(0.3f), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) return;

		_stoneScrapeAudio?.Play();

		var apparelTween = CreateTween();
		apparelTween.SetParallel(true);
		if (LeftApparel != null)
			apparelTween.TweenProperty(LeftApparel,  "position:z", LeftApparel.Position.Z  + 1.5f, 0.6f)
						.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
		if (RightApparel != null)
			apparelTween.TweenProperty(RightApparel, "position:z", RightApparel.Position.Z - 1.5f, 0.6f)
						.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
		foreach (var (light, _) in _moodLights)
			if (IsInstanceValid(light))
				apparelTween.TweenProperty(light, "light_color", MoodyColor, 0.7f)
							.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		apparelTween.Finished += () => ApparelOpened?.Invoke();

		await ToSignal(apparelTween, Tween.SignalName.Finished);
		if (!IsInsideTree()) return;

		// 5. Hold briefly while the table rises, then return rotation and FOV.
		await ToSignal(GetTree().CreateTimer(0.8f), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) return;

		{
			var returnTween = CreateTween();
			returnTween.SetParallel(true);
			if (Player != null)
				returnTween.TweenProperty(Player, "rotation:y", originalRotY, 0.35f)
						   .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
			if (camera != null)
				returnTween.TweenProperty(camera, "fov", originalFov, 0.35f)
						   .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
			await ToSignal(returnTween, Tween.SignalName.Finished);
			if (!IsInsideTree()) return;
		}

		if (pcc != null) { pcc.MouseLookEnabled = true; pcc.MovementEnabled = true; }

		WelcomeCompleted?.Invoke();

		// 6. NPC pointing animation once the player has full control.
		if (_animPlayer != null && _animPlayer.HasAnimation(TalkAnimation))
		{
			_animPlayer.Play(TalkAnimation, 0.3f);
			await ToSignal(_animPlayer, AnimationPlayer.SignalName.AnimationFinished);
			if (!IsInsideTree()) return;
			_animPlayer.Play(IdleAnimation, 0.3f);
		}

		_dialogBubble?.HideDialog();
	}

	// ── VFX helpers ───────────────────────────────────────────────────────────

	private void AddBurnFlash()
	{
		OmniLight3D flash = new OmniLight3D
		{
			TopLevel    = true,
			LightColor  = TransitionColor,
			LightEnergy = 0.0f,
			OmniRange   = 6.0f
		};
		GetParent().AddChild(flash);
		flash.GlobalPosition = GlobalPosition + Vector3.Up * 1.0f;

		Tween t = CreateTween();
		t.TweenProperty(flash, "light_energy", 5.0f, TransitionDuration * 0.15f);
		t.TweenProperty(flash, "light_energy", 0.0f, TransitionDuration * 0.85f);
		t.Finished += () => flash.QueueFree();
	}

	private void AddEmbers()
	{
		Vector3 spawnPos = GlobalPosition + Vector3.Up * 0.5f;

		CpuParticles3D particles = new CpuParticles3D { TopLevel = true };
		GetParent().AddChild(particles);
		particles.GlobalPosition     = spawnPos;
		particles.Amount             = 120;
		particles.Lifetime           = TransitionDuration * 1.5f;
		particles.OneShot            = true;
		particles.Explosiveness      = 0.85f;
		particles.EmissionShape      = CpuParticles3D.EmissionShapeEnum.Box;
		particles.EmissionBoxExtents = new Vector3(0.5f, 0.8f, 0.5f);
		particles.Direction          = new Vector3(0, 1, 0);
		particles.Spread             = 55.0f;
		particles.Gravity            = new Vector3(0, 2.0f, 0);
		particles.InitialVelocityMin = 1.0f;
		particles.InitialVelocityMax = 2.5f;
		particles.ScaleAmountMin     = 0.8f;
		particles.ScaleAmountMax     = 1.5f;

		Gradient gradient = new Gradient();
		gradient.SetColor(0, new Color(1, 1, 0.5f, 1));
		gradient.AddPoint(0.3f, new Color(1, 0.5f, 0.1f, 0.9f));
		gradient.SetColor(gradient.GetPointCount() - 1, new Color(0.8f, 0.1f, 0, 0));
		particles.ColorRamp = gradient;

		QuadMesh qm = new QuadMesh { Size = new Vector2(0.018f, 0.018f) };
		particles.Mesh = qm;

		StandardMaterial3D mat = new StandardMaterial3D
		{
			ShadingMode            = StandardMaterial3D.ShadingModeEnum.Unshaded,
			VertexColorUseAsAlbedo = true,
			BillboardMode          = StandardMaterial3D.BillboardModeEnum.Enabled,
			Transparency           = StandardMaterial3D.TransparencyEnum.Alpha
		};
		particles.MaterialOverride = mat;
		particles.Emitting         = true;

		GetTree().CreateTimer(particles.Lifetime + 0.5f).Timeout += () => particles.QueueFree();
	}

	private AnimationPlayer FindAnimationPlayer(Node node)
	{
		if (node is AnimationPlayer ap) return ap;
		foreach (Node child in node.GetChildren())
		{
			var found = FindAnimationPlayer(child);
			if (found != null) return found;
		}
		return null;
	}
}
