using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

public partial class Ophanim : Node3D
{
	// ── Animation ─────────────────────────────────────────────────────────────
	[Export] public float AnimationSpeed = 1.75f;

	// ── Hover ─────────────────────────────────────────────────────────────────
	[ExportGroup("Hover")]
	[Export] public float HoverBobAmplitude  = 0.12f;
	[Export] public float HoverSwayAmplitude = 0.15f;
	[Export] public float HoverRotateDeg     = 5.5f;
	[Export] public float HoverSpeed         = 0.7f;

	// ── Decode Settings ───────────────────────────────────────────────────────
	[ExportGroup("Decode")]
	[Export] public string DecodeText           = "[SUBJECT:P1] IS GOING TO DIE.";
	[Export] public float  GrowthInterval        = 0.01f;
	[Export] public float  ScrambleStartFraction = 0.2f;
	[Export] public float  Phase2Duration        = 0.5f;
	[Export] public float  Phase2FlipsPerSec     = 14f;
	[Export] public float  Phase3StaggerInterval = 0.06f;
	[Export] public float  RedactionRate         = 0.1f;
	[Export] public float  HoldDuration           = 5.0f;
	[Export] public int    BubbleViewportWidth    = 1400;
	[Export] public int    BubbleViewportHeight   = 280;
	[Export] public float  BubbleWorldScale       = 2.2f;

	// ── Greeting ──────────────────────────────────────────────────────────────
	[ExportGroup("Greeting")]
	[Export] public string GreetingMsg1 = "WELCOME TO THE TABLE.";
	[Export] public string GreetingMsg2 = "THE ORDER MUST BE SET BEFORE THE GAME BEGINS.";
	[Export] public string GreetingMsg3 = "THE REVOLVER WILL ROTATE. A CLICK PASSES. A SHOT DECIDES.";
	[Export] public float  GreetingHoldDuration = 2.5f;
	[Export] public float  GreetingMsgPause     = 0.4f;

	// ── Descent ───────────────────────────────────────────────────────────────
	[ExportGroup("Descent")]
	[Export] public float DescentHiddenOffset = 3.5f;
	[Export] public float DescentDuration     = 2.5f;

	// ── Audio ─────────────────────────────────────────────────────────────────
	[ExportGroup("Audio")]
	[Export] public float AmbientVolumeDb = -18f;
	[Export] public float GarbledVolumeDb = -12f;

	// ── Initiative ────────────────────────────────────────────────────────────
	[ExportGroup("Initiative")]
	[Export] public PackedScene InitiativeGunScene;
	[Export] public float GunSpawnDropY      = -0.40f;
	[Export] public float GunSpawnDropTime   = 0.6f;
	[Export] public float GunSpawnReadyDelay = 1.1f;
	[Export] public float GunRotateTime   = 2.0f;
	[Export] public float GunPreShotPause = 0.3f;
	[Export] public float GunMisfirePause = 1.0f;
	[Export] public float GunBangPause    = 2.0f;

	// ── Character pools ────────────────────────────────────────────────────────
	private static readonly char[] _blockChars = { '█', '▓', '▒', '░' };
	private static readonly char[] _symbolChars =
	{
		'Ψ','Ω','Σ','ø','†','∆','Ͽ','Ξ','Λ','Π','Φ','Χ','Θ',
		'Γ','ζ','η','μ','ξ','π','ρ','φ','χ','ψ','ω',
		'∫','∂','∇','≈','≠','∞','√','⊕','⊗',
	};
	private static readonly char[] _redactedChars = { '█', '▓', '▒', '░', '†', 'Σ', 'Ω' };

	// ── Nodes ─────────────────────────────────────────────────────────────────
	private AudioStreamPlayer3D _garbledAudio;
	private AudioStreamPlayer3D _droneAudio;
	private AudioStreamPlayer3D _wingFlapAudio;
	private DialogBubble        _dialogBubble;
	private Interactable        _interactable;
	private Tween               _flickerTween;
	private bool                _isDecoding;
	private bool                _inGreeting;
	private bool                _garbledShouldLoop;

	// ── Descent ───────────────────────────────────────────────────────────────
	private Vector3 _visibleBasePosition;
	private Vector3 _baseRotation;
	private float   _descendYOffset;
	private bool    _hasActivated;

	// ── Initiative Gun ────────────────────────────────────────────────────────
	private Vector3 _redChairWorldPos;
	private Gun   _initiativeGun;
	private bool  _greetingDone;
	private (Vector3 pos, string result)[] _pendingShots;
	private string _pendingWinnerName = "";

	private readonly Random _rng = new Random();

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_visibleBasePosition = Position;
		_baseRotation        = Rotation;
		_descendYOffset      = DescentHiddenOffset;

		var animPlayer = FindAnimationPlayer(this);
		if (animPlayer != null && animPlayer.HasAnimation("Animation"))
		{
			var anim = animPlayer.GetAnimation("Animation");
			anim.LoopMode = Animation.LoopModeEnum.Linear;
			animPlayer.SpeedScale = AnimationSpeed;
			animPlayer.Play("Animation");
		}

		// Store audio refs but do NOT play — sounds start only after descent completes.
		_droneAudio    = GetNodeOrNull<AudioStreamPlayer3D>("DroneAudio");
		_wingFlapAudio = GetNodeOrNull<AudioStreamPlayer3D>("WingFlapAudio");
		_garbledAudio  = GetNodeOrNull<AudioStreamPlayer3D>("GarbledSpeech");
		_dialogBubble  = GetNodeOrNull<DialogBubble>("DialogBubble");
		_interactable  = GetNodeOrNull<Interactable>("Interactable");

		if (_garbledAudio != null)
			_garbledAudio.Finished += OnGarbledFinished;

		if (_interactable != null)
			_interactable.Interacted += OnInteracted;

		if (_dialogBubble?.Viewport != null)
			_dialogBubble.Viewport.Size = new Vector2I(BubbleViewportWidth, BubbleViewportHeight);
	}

	public override void _Process(double delta)
	{
		float t = (float)Time.GetTicksMsec() / 1000.0f * HoverSpeed;
		Position = _visibleBasePosition + new Vector3(0f, _descendYOffset, 0f) + new Vector3(
			Mathf.Sin(t * 0.53f) * HoverSwayAmplitude,
			Mathf.Sin(t * 0.80f) * HoverBobAmplitude,
			Mathf.Sin(t * 0.37f) * HoverSwayAmplitude * 0.6f
		);
		float rotRad = Mathf.DegToRad(HoverRotateDeg);
		Rotation = _baseRotation + new Vector3(
			Mathf.Sin(t * 0.60f) * rotRad * 0.5f,
			Mathf.Sin(t * 0.45f) * rotRad,
			Mathf.Sin(t * 0.70f) * rotRad * 0.7f
		);
	}

	// ── Ambient Audio ─────────────────────────────────────────────────────────

	private void EnableAmbientAudio()
	{
		StartLoopingAudio(_droneAudio);
		StartLoopingAudio(_wingFlapAudio);
	}

	private void StartLoopingAudio(AudioStreamPlayer3D player)
	{
		if (player == null) return;
		player.VolumeDb = AmbientVolumeDb;
		player.Finished += () => player.Play();
		player.Play();
	}

	// ── Garbled Audio ─────────────────────────────────────────────────────────

	private void OnGarbledFinished()
	{
		if (_garbledShouldLoop) _garbledAudio?.Play();
	}

	private void StartGarbledAudio()
	{
		if (_garbledAudio == null) return;
		_garbledAudio.VolumeDb = GarbledVolumeDb;
		_garbledShouldLoop = true;
		_garbledAudio.Play((float)(_rng.NextDouble() * 5.0));
	}

	private void StopGarbledAudio()
	{
		_garbledShouldLoop = false;
		_garbledAudio?.Stop();
	}

	// ── Interaction ───────────────────────────────────────────────────────────

	private async void OnInteracted()
	{
		if (_isDecoding || _inGreeting) return;
		await RunDecodeSequenceAsync(DecodeText, HoldDuration);
	}

	// ── Descent ───────────────────────────────────────────────────────────────

	/// <summary>Called by TableManager when chairs_locked is received. Descends
	/// from the ceiling, enables ambient audio, then runs the greeting sequence.</summary>
	public void DescendAndActivate(Vector3 redChairWorldPos)
	{
		if (_hasActivated) return;
		_hasActivated    = true;
		_redChairWorldPos = redChairWorldPos;

		// Sounds start NOW so they accompany the descent.
		EnableAmbientAudio();

		var tween = CreateTween();
		tween.TweenMethod(
			Callable.From((float v) => _descendYOffset = v),
			DescentHiddenOffset, 0f, DescentDuration)
			.SetTrans(Tween.TransitionType.Cubic)
			.SetEase(Tween.EaseType.Out);
		tween.Finished += () =>
		{
			_descendYOffset = 0f;
			RunGreetingSequence();
		};
	}

	// ── Greeting Sequence ─────────────────────────────────────────────────────

	private async void RunGreetingSequence()
	{
		_inGreeting = true;

		await RunDecodeSequenceAsync(GreetingMsg1, GreetingHoldDuration);
		if (!IsInsideTree()) return;
		await ToSignal(GetTree().CreateTimer(GreetingMsgPause), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) return;

		await RunDecodeSequenceAsync(GreetingMsg2, GreetingHoldDuration);
		if (!IsInsideTree()) return;
		await ToSignal(GetTree().CreateTimer(GreetingMsgPause), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) return;

		await RunDecodeSequenceAsync(GreetingMsg3, GreetingHoldDuration);
		if (!IsInsideTree()) return;
		await ToSignal(GetTree().CreateTimer(GreetingMsgPause), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) return;

		_inGreeting  = false;
		_greetingDone = true;
		SpawnInitiativeGun();

		if (_pendingShots != null)
		{
			var shots      = _pendingShots;
			var winnerName = _pendingWinnerName;
			_pendingShots      = null;
			_pendingWinnerName = "";
			RunInitiativeGunSequence(shots, winnerName);
		}
	}

	// ── Initiative Gun ────────────────────────────────────────────────────────

	/// <summary>Called by TableManager when initiative_sequence is received.
	/// If the greeting is still playing the data is queued and played afterwards.</summary>
	public void StartInitiativeSequence((Vector3 pos, string result)[] shots, string winnerName)
	{
		if (_greetingDone)
			RunInitiativeGunSequence(shots, winnerName);
		else
		{
			_pendingShots      = shots;
			_pendingWinnerName = winnerName;
		}
	}

	private void SpawnInitiativeGun()
	{
		if (InitiativeGunScene == null)
		{
			GD.PrintErr("[Ophanim] InitiativeGunScene is not set.");
			return;
		}

		var instance = InitiativeGunScene.Instantiate<Node3D>();
		AddChild(instance);

		// TopLevel detaches the gun from Ophanim's rotating/bobbing transform so
		// it sits stably in world space and global_rotation tweens work cleanly.
		instance.TopLevel       = true;
		instance.GlobalPosition = GlobalPosition;
		instance.Scale          = Vector3.Zero;

		// Aim at red chair immediately so the gun is already aligned during the
		// pop-in and descent animations — no mid-air snap visible to the player.
		Vector3 toRed = _redChairWorldPos - GlobalPosition;
		toRed.Y = 0f;
		if (_redChairWorldPos != Vector3.Zero && toRed.LengthSquared() > 0.001f)
			instance.GlobalRotation = new Vector3(0, Mathf.Atan2(toRed.X, toRed.Z), 0);
		else
			instance.GlobalRotation = Vector3.Zero;

		_initiativeGun = instance as Gun;
		if (_initiativeGun == null)
			GD.PrintErr("[Ophanim] InitiativeGunScene root is not a Gun node.");
		else
			_initiativeGun.SetInteractionEnabled(false);

		// Pop in, then descend to the operating position.
		float dropTargetY = GlobalPosition.Y + GunSpawnDropY;
		var spawnTween = CreateTween();
		spawnTween.TweenProperty(instance, "scale", Vector3.One, 0.4f)
				  .SetTrans(Tween.TransitionType.Back)
				  .SetEase(Tween.EaseType.Out);
		spawnTween.TweenProperty(instance, "global_position:y", dropTargetY, GunSpawnDropTime)
				  .SetTrans(Tween.TransitionType.Cubic)
				  .SetEase(Tween.EaseType.Out);
	}

	private async void RunInitiativeGunSequence((Vector3 pos, string result)[] shots, string winnerName)
	{
		if (_initiativeGun == null) return;

		// Wait for pop-in (0.4s) + descent (GunSpawnDropTime) to finish.
		await ToSignal(GetTree().CreateTimer(GunSpawnReadyDelay), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) return;

		// Gun is already aimed at red chair from SpawnInitiativeGun — hold briefly for tension.
		await ToSignal(GetTree().CreateTimer(0.5f), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) return;

		bool announceDone = false;
		foreach (var (chairWorldPos, result) in shots)
		{
			// Aim from the gun's own world position (TopLevel, stable in space).
			Vector3 toTarget = chairWorldPos - _initiativeGun.GlobalPosition;
			toTarget.Y = 0f;
			if (toTarget.LengthSquared() > 0.001f)
			{
				toTarget = toTarget.Normalized();
				float targetYAngle = Mathf.Atan2(toTarget.X, toTarget.Z);

				var rotateTween = CreateTween();
				rotateTween.TweenProperty(_initiativeGun, "global_rotation:y", targetYAngle, GunRotateTime)
						   .SetTrans(Tween.TransitionType.Cubic)
						   .SetEase(Tween.EaseType.Out);
				await ToSignal(rotateTween, Tween.SignalName.Finished);
				if (!IsInsideTree()) return;
			}

			await ToSignal(GetTree().CreateTimer(GunPreShotPause), SceneTreeTimer.SignalName.Timeout);
			if (!IsInsideTree()) return;

			bool isBang = result == "bang";
			_initiativeGun.FireInitiativeShot(isBang);

			float holdTime = isBang ? GunBangPause : GunMisfirePause;
			await ToSignal(GetTree().CreateTimer(holdTime), SceneTreeTimer.SignalName.Timeout);
			if (!IsInsideTree()) return;

			if (isBang)
			{
				await ShowWinnerAnnouncementAsync(winnerName);
				if (!IsInsideTree()) return;
				announceDone = true;
				break;
			}
		}

		// Despawn the gun.
		if (IsInstanceValid(_initiativeGun))
		{
			var despawnTween = CreateTween();
			despawnTween.TweenProperty(_initiativeGun, "scale", Vector3.Zero, 0.3f)
						.SetTrans(Tween.TransitionType.Back)
						.SetEase(Tween.EaseType.In);
			despawnTween.Finished += () =>
			{
				if (IsInstanceValid(_initiativeGun)) _initiativeGun.QueueFree();
				_initiativeGun = null;
			};
		}
	}

	private async Task ShowWinnerAnnouncementAsync(string winnerName)
	{
		string name = string.IsNullOrEmpty(winnerName) ? "???" : winnerName.ToUpper();
		await RunDecodeSequenceAsync($"{name} GOES FIRST. 2 RANDOM ITEMS HAVE BEEN GRANTED.", GreetingHoldDuration);
	}

	// ── Bubble Helpers ────────────────────────────────────────────────────────

	private void ShowBubble(string initialText)
	{
		if (_dialogBubble == null) return;
		if (_dialogBubble.TextLabel != null)
		{
			_dialogBubble.TextLabel.Text         = initialText;
			_dialogBubble.TextLabel.VisibleRatio = 1.0f;
		}
		if (_dialogBubble.DisplaySprite != null)
		{
			_dialogBubble.DisplaySprite.Scale    = Vector3.One;
			_dialogBubble.DisplaySprite.Modulate = Colors.White;
		}

		_dialogBubble.Visible = true;
		_dialogBubble.Scale   = Vector3.Zero;

		var popup = CreateTween();
		popup.TweenProperty(_dialogBubble, "scale", Vector3.One * BubbleWorldScale, 0.3f)
			 .SetTrans(Tween.TransitionType.Cubic)
			 .SetEase(Tween.EaseType.Out);
	}

	private void UpdateBubbleText(string text)
	{
		if (_dialogBubble?.TextLabel == null) return;
		_dialogBubble.TextLabel.Text = text;
	}

	// ── Decode Sequence ───────────────────────────────────────────────────────

	private async Task RunDecodeSequenceAsync(string target, float holdDuration)
	{
		_isDecoding = true;

		int len      = target.Length;
		var display  = new char[len];
		var resolved = new bool[len];

		for (int i = 0; i < len; i++)
		{
			display[i]  = target[i];
			resolved[i] = !ShouldGlitch(target[i]);
		}

		var glitchIndices = new List<int>();
		for (int i = 0; i < len; i++)
			if (!resolved[i]) glitchIndices.Add(i);

		var redacted    = new HashSet<int>();
		int redactCount = Mathf.Max(1, (int)(glitchIndices.Count * RedactionRate));
		var eligible    = new List<int>(glitchIndices);
		while (redacted.Count < redactCount && eligible.Count > 0)
		{
			int pick = _rng.Next(eligible.Count);
			redacted.Add(eligible[pick]);
			eligible.RemoveAt(pick);
		}

		for (int i = 0; i < len; i++)
			display[i] = resolved[i] ? target[i] : RandomBlock();

		// ── Phase 0: Growth ───────────────────────────────────────────────────
		ShowBubble("");
		StartGarbledAudio();

		int scrambleAt = Mathf.Max(1, (int)(len * ScrambleStartFraction));

		if (_dialogBubble?.DisplaySprite != null)
		{
			_flickerTween = CreateTween().SetLoops();
			_flickerTween.TweenProperty(_dialogBubble.DisplaySprite, "modulate:a", 0.4f, 0.07f);
			_flickerTween.TweenProperty(_dialogBubble.DisplaySprite, "modulate:a", 1.0f, 0.10f);
		}

		for (int i = 0; i < len; i++)
		{
			if (i == scrambleAt)
			{
				_flickerTween?.Kill();
				_flickerTween = null;
				if (_dialogBubble?.DisplaySprite != null)
					_dialogBubble.DisplaySprite.Modulate = Colors.White;
			}
			if (i >= scrambleAt)
				for (int j = 0; j < i; j++)
					if (!resolved[j]) display[j] = RandomSymbol();

			UpdateBubbleText(new string(display, 0, i + 1));
			await ToSignal(GetTree().CreateTimer(GrowthInterval), SceneTreeTimer.SignalName.Timeout);
			if (!IsInsideTree()) return;
		}

		_flickerTween?.Kill();
		_flickerTween = null;
		if (_dialogBubble?.DisplaySprite != null)
			_dialogBubble.DisplaySprite.Modulate = Colors.White;

		// ── Phase 2: Scramble ─────────────────────────────────────────────────
		float flipInterval = 1.0f / Mathf.Max(1f, Phase2FlipsPerSec);
		float elapsed2     = 0f;
		while (elapsed2 < Phase2Duration)
		{
			for (int i = 0; i < len; i++)
				if (!resolved[i]) display[i] = RandomSymbol();
			UpdateBubbleText(new string(display));

			await ToSignal(GetTree().CreateTimer(flipInterval), SceneTreeTimer.SignalName.Timeout);
			if (!IsInsideTree()) return;
			elapsed2 += flipInterval;
		}

		// ── Phase 3: Resolution ───────────────────────────────────────────────
		foreach (int i in glitchIndices)
		{
			display[i]  = redacted.Contains(i) ? RandomRedacted() : target[i];
			resolved[i] = true;

			for (int j = 0; j < len; j++)
				if (!resolved[j]) display[j] = RandomSymbol();

			UpdateBubbleText(new string(display));

			await ToSignal(GetTree().CreateTimer(Phase3StaggerInterval), SceneTreeTimer.SignalName.Timeout);
			if (!IsInsideTree()) return;
		}

		StopGarbledAudio();

		await ToSignal(GetTree().CreateTimer(holdDuration), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) return;

		_dialogBubble?.HideDialog();
		_isDecoding = false;
	}

	// ── Helpers ───────────────────────────────────────────────────────────────

	private static bool ShouldGlitch(char c) => char.IsLetterOrDigit(c);
	private char RandomBlock()    => _blockChars[_rng.Next(_blockChars.Length)];
	private char RandomSymbol()   => _symbolChars[_rng.Next(_symbolChars.Length)];
	private char RandomRedacted() => _redactedChars[_rng.Next(_redactedChars.Length)];

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
