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
	[Export] public float HoverBobAmplitude      = 0.12f;
	[Export] public float HoverSwayAmplitude     = 0.15f;
	[Export] public float HoverRotateDeg         = 5.5f;
	[Export] public float HoverSpeed             = 0.7f;
	[Export] public float RestingSwayMultiplier  = 0.25f;

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
	[Export] public float RestingOffset       = 0.5f;

	// ── Audio ─────────────────────────────────────────────────────────────────
	[ExportGroup("Audio")]
	[Export] public float AmbientVolumeDb = -18f;
	[Export] public float GarbledVolumeDb = -12f;

	// ── Initiative ────────────────────────────────────────────────────────────
	[ExportGroup("Initiative")]
	[Export] public PackedScene   InitiativeGunScene;
	[Export] public float GunRestYAboveTable  = 0.45f;
	[Export] public float GunSpawnDropTime   = 0.6f;
	[Export] public float GunSpawnReadyDelay = 1.1f;
	[Export] public float GunRotateTime   = 2.0f;
	[Export] public float GunPreShotPause = 0.3f;
	[Export] public float GunMisfirePause = 1.0f;
	[Export] public float GunBangPause    = 2.0f;

	// ── Item Grants ───────────────────────────────────────────────────────────
	[ExportGroup("Item Grants")]
	[Export] public AudioStream BrimstoneRaySound;
	[Export] public float BrimstoneRayVolumeDb = -10f;
	[Export] public float ItemRayGrowTime      = 0.18f;
	[Export] public float ItemRayHoldTime      = 0.22f;
	[Export] public float ItemRayShrinkTime    = 0.15f;
	[Export] public float ItemRayRadius        = 0.018f;
	[Export] public float ItemGrantPause       = 0.2f;
	[Export] public string LivesMessage          = "EACH PLAYER BEGINS WITH 3 LIVES. LOSE THEM ALL — ELIMINATED.";
	[Export] public string GoldenSquaresMessage  = "FOUR SQUARES ON THIS BOARD WILL BE MARKED IN GOLD.";
	[Export] public string GoldenSquaresMessage2 = "LAND ON ONE — AND FORTUNE WILL DECIDE YOUR FATE.";
	[Export] public string BeginMessage          = "BEGIN.";
	[Export] public float  BeginHoldDuration     = 0.5f;
	[Export] public float  PieceGrantPause       = 0.1f;
	[Export] public float  PieceRayVolumeDb        = -23f;
	[Export] public float  GoldenRayVolumeDb       = -23f;
	[Export] public string SwordSpawnMessage      = "YOU ALL HAVE 30 SECONDS. DO NOT LET THIS DAGGER FALL.";
	[Export] public float  SwordSpawnHoldDuration = 2.0f;

	// ── Golden Square ─────────────────────────────────────────────────────────
	[ExportGroup("Golden Square")]
	[Export] public float  GoldenDescentDuration = 1.5f;
	[Export] public float  GoldenHoldDuration    = 2.8f;
	[Export] public string GoldenLuckyMessage    = "LUCKY YOU...";
	[Export] public float  VibrateDuration       = 1.0f;
	[Export] public float  VibrateAmplitude      = 0.09f;
	[Export] public float  VibrateFrequency      = 20f;

	// ── Signals ───────────────────────────────────────────────────────────────
	/// <summary>Fired after the initiative gun sequence ends and the gun despawns.</summary>
	[Signal] public delegate void InitiativeSequenceCompletedEventHandler();

	/// <summary>Fired when the player interacts with Ophanim during the gun decision window (skip = eat for free).</summary>
	[Signal] public delegate void GunSkipRequestedEventHandler();

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
	private int                 _decodeSequenceId;
	private bool                _inGreeting;
	private bool                _garbledShouldLoop;
	private bool                _gunDecisionActive = false;
	private Label3D             _gunDecisionLabel  = null;

	// ── Descent ───────────────────────────────────────────────────────────────
	private Vector3 _visibleBasePosition;
	private Vector3 _baseRotation;
	private float   _descendYOffset;
	private float   _swayMultiplier = 1f;
	private bool    _hasActivated;
	private Vector3 _boardCenterWorld;

	// ── Vibrate ───────────────────────────────────────────────────────────────
	private float _vibrateStartTime = -1f;
	private float _vibrateDuration  = 0f;

	public float CurrentDescendOffset => _descendYOffset;

	// ── Initiative Gun ────────────────────────────────────────────────────────
	private Vector3 _redChairWorldPos;
	private Gun   _initiativeGun;
	private bool  _greetingDone;
	private (Vector3 pos, string result)[] _pendingShots;
	private string _pendingWinnerName = "";

	private (Vector3 worldPos, System.Action onSpawn)[] _pendingItemGrants;
	private (Vector3 worldPos, System.Action onSpawn)[] _pendingGoldenGrants;
	private (Vector3 worldPos, System.Action onSpawn)[] _pendingShellGrants;
	private (Vector3 worldPos, System.Action onSpawn)[] _pendingPieceGrants;

	private readonly Random _rng = new Random();

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_visibleBasePosition = Position;
		_baseRotation        = Rotation;
		_descendYOffset      = DescentHiddenOffset;
		FocusController.FocusStateChanged += OnFocusStateChanged;

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
		bool focusHide = FocusController.Instance?.IsFocused ?? false;
		Visible = !focusHide;
		if (focusHide) return;

		float targetMult = _descendYOffset >= RestingOffset * 0.5f ? RestingSwayMultiplier : 1f;
		_swayMultiplier  = Mathf.Lerp(_swayMultiplier, targetMult, (float)delta * 2f);

		float t = (float)Time.GetTicksMsec() / 1000.0f * HoverSpeed;

		Vector3 vibratePos = Vector3.Zero;
		if (_vibrateDuration > 0f)
		{
			float elapsed = (float)(Time.GetTicksMsec() / 1000.0) - _vibrateStartTime;
			if (elapsed < _vibrateDuration)
			{
				float fadeIn  = Mathf.Clamp(elapsed / 0.25f, 0f, 1f);
				float fadeOut = Mathf.Clamp(1f - (elapsed - _vibrateDuration + 0.25f) / 0.25f, 0f, 1f);
				float amp = VibrateAmplitude * fadeIn * fadeOut;
				vibratePos = new Vector3(
					Mathf.Sin(elapsed * VibrateFrequency * Mathf.Tau) * amp,
					Mathf.Abs(Mathf.Sin(elapsed * VibrateFrequency * 0.5f * Mathf.Tau)) * amp * 0.4f,
					Mathf.Cos(elapsed * VibrateFrequency * 0.7f * Mathf.Tau) * amp * 0.6f
				);
			}
			else
			{
				_vibrateDuration = 0f;
			}
		}

		Position = _visibleBasePosition + new Vector3(0f, _descendYOffset, 0f) + vibratePos + new Vector3(
			Mathf.Sin(t * 0.53f) * HoverSwayAmplitude * _swayMultiplier,
			Mathf.Sin(t * 0.80f) * HoverBobAmplitude  * _swayMultiplier,
			Mathf.Sin(t * 0.37f) * HoverSwayAmplitude * 0.6f * _swayMultiplier
		);
		float rotRad = Mathf.DegToRad(HoverRotateDeg);
		Rotation = _baseRotation + new Vector3(
			Mathf.Sin(t * 0.60f) * rotRad * 0.5f,
			Mathf.Sin(t * 0.45f) * rotRad,
			Mathf.Sin(t * 0.70f) * rotRad * 0.7f
		) * _swayMultiplier;
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
		if (_gunDecisionActive)
		{
			EmitSignal(SignalName.GunSkipRequested);
			return;
		}
		if (_isDecoding || _inGreeting) return;
		await RunDecodeSequenceAsync(DecodeText, HoldDuration);
	}

	// ── Descent ───────────────────────────────────────────────────────────────

	/// <summary>Called by TableManager when chairs_locked is received. Descends
	/// from the ceiling, enables ambient audio, then runs the greeting sequence.</summary>
	public void DescendAndActivate(Vector3 redChairWorldPos, Vector3 boardCenterWorld)
	{
		if (_hasActivated) return;
		_hasActivated     = true;
		_redChairWorldPos = redChairWorldPos;
		_boardCenterWorld = boardCenterWorld;

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

	// ── Golden Square Sequence ────────────────────────────────────────────────

	/// <summary>Re-descend after the initiative sequence has finished and Ophanim is hidden.</summary>
	public void ReDescend()
	{
		var tween = CreateTween();
		tween.TweenMethod(
			Callable.From((float v) => _descendYOffset = v),
			_descendYOffset, 0f, GoldenDescentDuration)
			.SetTrans(Tween.TransitionType.Cubic)
			.SetEase(Tween.EaseType.Out);
		tween.Finished += () => _descendYOffset = 0f;
	}

	/// <summary>Run a decode sequence and wait for it to finish. No other side effects.</summary>
	public async System.Threading.Tasks.Task SayAsync(string message, float holdDuration = -1f)
	{
		if (holdDuration < 0f) holdDuration = GoldenHoldDuration;
		await RunDecodeSequenceAsync(message, holdDuration);
	}

	/// <summary>Rapidly shake the angel for VibrateDuration seconds as if deciding, then settle.</summary>
	public async System.Threading.Tasks.Task VibrateDecidingAsync()
	{
		_vibrateStartTime = (float)(Time.GetTicksMsec() / 1000.0);
		_vibrateDuration  = VibrateDuration;

		AudioStreamPlayer3D vibrateAudio = null;
		var vibrateClip = GD.Load<AudioStream>("res://Assets/Sound FX/gear_vibrating.wav");
		if (vibrateClip != null)
		{
			vibrateAudio = new AudioStreamPlayer3D { Stream = vibrateClip, VolumeDb = -6f };
			AddChild(vibrateAudio);
			vibrateAudio.Play();
		}

		await ToSignal(GetTree().CreateTimer(VibrateDuration), SceneTreeTimer.SignalName.Timeout);
		_vibrateDuration = 0f;

		var clingClip = GD.Load<AudioStream>("res://Assets/Sound FX/success_cling.wav");
		if (clingClip != null)
		{
			var cling = new AudioStreamPlayer3D { Stream = clingClip, VolumeDb = -4f };
			AddChild(cling);
			cling.Play();
			cling.Finished += () => cling.QueueFree();
		}

		if (vibrateAudio != null)
		{
			const float fadeTime = 1.0f;
			var fadeTween = vibrateAudio.CreateTween();
			fadeTween.TweenProperty(vibrateAudio, "volume_db", -80f, fadeTime);
			await ToSignal(GetTree().CreateTimer(fadeTime), SceneTreeTimer.SignalName.Timeout);
			vibrateAudio.Stop();
			vibrateAudio.QueueFree();
		}
	}

	/// <summary>Say the grant announcement while simultaneously shooting the item ray, then ascend and hide.
	/// Awaitable — completes after both speech and ray finish.</summary>
	public async System.Threading.Tasks.Task AnnounceGoldenGrant(
		string message, Vector3 itemWorldPos, System.Action onItemGranted)
	{
		// Fire ray immediately — it finishes long before the speech, so the item appears while talking.
		_ = ShootItemRay(itemWorldPos, onItemGranted);
		await RunDecodeSequenceAsync(message, GoldenHoldDuration);
		if (!IsInsideTree()) return;
		AscendAndHide();
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

		// Fire the last greeting and spawn the gun concurrently — gun arrives while Ophanim is still talking.
		_ = RunDecodeSequenceAsync(GreetingMsg3, GreetingHoldDuration);

		_inGreeting  = false;
		_greetingDone = true;
		SpawnInitiativeGun();

		if (_pendingShots != null)
		{
			var shots        = _pendingShots;
			var winnerName   = _pendingWinnerName;
			var itemGrants   = _pendingItemGrants;
			var goldenGrants = _pendingGoldenGrants;
			var shellGrants  = _pendingShellGrants;
			var pieceGrants  = _pendingPieceGrants;
			_pendingShots        = null;
			_pendingWinnerName   = "";
			_pendingItemGrants   = null;
			_pendingGoldenGrants = null;
			_pendingShellGrants  = null;
			_pendingPieceGrants  = null;
			RunInitiativeGunSequence(shots, winnerName, itemGrants, goldenGrants, shellGrants, pieceGrants);
		}
	}

	// ── Initiative Gun ────────────────────────────────────────────────────────

	/// <summary>Called by TableManager when initiative_sequence is received.
	/// If the greeting is still playing the data is queued and played afterwards.</summary>
	public void StartInitiativeSequence(
		(Vector3 pos, string result)[] shots,
		string winnerName,
		(Vector3 worldPos, System.Action onSpawn)[] itemGrants   = null,
		(Vector3 worldPos, System.Action onSpawn)[] goldenGrants = null,
		(Vector3 worldPos, System.Action onSpawn)[] shellGrants  = null,
		(Vector3 worldPos, System.Action onSpawn)[] pieceGrants  = null)
	{
		if (_greetingDone)
			RunInitiativeGunSequence(shots, winnerName, itemGrants, goldenGrants, shellGrants, pieceGrants);
		else
		{
			_pendingShots        = shots;
			_pendingWinnerName   = winnerName;
			_pendingItemGrants   = itemGrants;
			_pendingGoldenGrants = goldenGrants;
			_pendingShellGrants  = shellGrants;
			_pendingPieceGrants  = pieceGrants;
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
		instance.Scale          = Vector3.One * 0.001f;

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

		// Pop in at Ophanim's center, then tween to a fixed position above the table center
		// so the resting height is never affected by Ophanim's bobbing animation.
		var restTarget = new Vector3(
			_boardCenterWorld.X,
			_boardCenterWorld.Y + GunRestYAboveTable,
			_boardCenterWorld.Z);
		var spawnTween = CreateTween();
		spawnTween.TweenProperty(instance, "scale", Vector3.One, 0.4f)
				  .SetTrans(Tween.TransitionType.Back)
				  .SetEase(Tween.EaseType.Out);
		spawnTween.TweenProperty(instance, "global_position", restTarget, GunSpawnDropTime)
				  .SetTrans(Tween.TransitionType.Cubic)
				  .SetEase(Tween.EaseType.Out);
	}

	private async void RunInitiativeGunSequence(
		(Vector3 pos, string result)[] shots,
		string winnerName,
		(Vector3 worldPos, System.Action onSpawn)[] itemGrants,
		(Vector3 worldPos, System.Action onSpawn)[] goldenGrants,
		(Vector3 worldPos, System.Action onSpawn)[] shellGrants,
		(Vector3 worldPos, System.Action onSpawn)[] pieceGrants)
	{
		if (_initiativeGun == null) return;

		// Wait for pop-in (0.4s) + descent (GunSpawnDropTime) to finish.
		await ToSignal(GetTree().CreateTimer(GunSpawnReadyDelay), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) return;

		// Gun is already aimed at red chair from SpawnInitiativeGun — hold briefly for tension.
		await ToSignal(GetTree().CreateTimer(0.5f), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) return;

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
				break;
			}
		}

		// Despawn the gun, then hand off to item grants before signalling completion.
		if (IsInstanceValid(_initiativeGun))
		{
			// Disable all physics inside the gun before scaling to zero — Jolt rejects singular transforms.
			SetPhysicsEnabled(_initiativeGun, false);
			var despawnTween = CreateTween();
			despawnTween.TweenProperty(_initiativeGun, "scale", Vector3.Zero, 0.3f)
						.SetTrans(Tween.TransitionType.Back)
						.SetEase(Tween.EaseType.In);
			despawnTween.Finished += () =>
			{
				if (IsInstanceValid(_initiativeGun)) _initiativeGun.QueueFree();
				_initiativeGun = null;
				RunItemGrantsSequence(itemGrants, goldenGrants, shellGrants, pieceGrants);
			};
		}
		else
		{
			RunItemGrantsSequence(itemGrants, goldenGrants, shellGrants, pieceGrants);
		}
	}

	// ── Item Grant Rays ───────────────────────────────────────────────────────

	private async void RunItemGrantsSequence(
		(Vector3 worldPos, System.Action onSpawn)[] grants,
		(Vector3 worldPos, System.Action onSpawn)[] goldenGrants,
		(Vector3 worldPos, System.Action onSpawn)[] shellGrants,
		(Vector3 worldPos, System.Action onSpawn)[] pieceGrants = null)
	{
		if (grants != null && grants.Length > 0)
		{
			await ToSignal(GetTree().CreateTimer(0.25f), SceneTreeTimer.SignalName.Timeout);
			if (!IsInsideTree()) return;

			foreach (var (worldPos, onSpawn) in grants)
			{
				await ShootItemRay(worldPos, onSpawn);
				if (!IsInsideTree()) return;
				await ToSignal(GetTree().CreateTimer(ItemGrantPause), SceneTreeTimer.SignalName.Timeout);
				if (!IsInsideTree()) return;
			}
		}

		// Introduce the golden squares before revealing them.
		if (goldenGrants != null && goldenGrants.Length > 0)
		{
			await ToSignal(GetTree().CreateTimer(0.3f), SceneTreeTimer.SignalName.Timeout);
			if (!IsInsideTree()) return;
			await RunDecodeSequenceAsync(GoldenSquaresMessage, GreetingHoldDuration * 0.55f);
			if (!IsInsideTree()) return;
			await ToSignal(GetTree().CreateTimer(GreetingMsgPause), SceneTreeTimer.SignalName.Timeout);
			if (!IsInsideTree()) return;
			// Fire all golden square rays concurrently as the second message begins.
			foreach (var (worldPos, onSpawn) in goldenGrants)
				_ = ShootItemRay(worldPos, onSpawn, GoldenRayVolumeDb);

			await RunDecodeSequenceAsync(GoldenSquaresMessage2, GreetingHoldDuration * 0.55f);
			if (!IsInsideTree()) return;
		}

		// Explain the lives system first, then reveal the shells.
		await ToSignal(GetTree().CreateTimer(0.3f), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) return;
		await RunDecodeSequenceAsync(LivesMessage, GreetingHoldDuration * 0.7f);
		if (!IsInsideTree()) return;

		if (shellGrants != null && shellGrants.Length > 0)
		{
			await ToSignal(GetTree().CreateTimer(0.3f), SceneTreeTimer.SignalName.Timeout);
			if (!IsInsideTree()) return;

			foreach (var (worldPos, onSpawn) in shellGrants)
			{
				await ShootItemRay(worldPos, onSpawn);
				if (!IsInsideTree()) return;
				await ToSignal(GetTree().CreateTimer(ItemGrantPause), SceneTreeTimer.SignalName.Timeout);
				if (!IsInsideTree()) return;
			}
		}

		if (pieceGrants != null && pieceGrants.Length > 0)
		{
			await ToSignal(GetTree().CreateTimer(0.2f), SceneTreeTimer.SignalName.Timeout);
			if (!IsInsideTree()) return;
			await RunDecodeSequenceAsync(BeginMessage, BeginHoldDuration);
			if (!IsInsideTree()) return;

			// Cubilete (index 0) fires alone.
			await ShootItemRay(pieceGrants[0].worldPos, pieceGrants[0].onSpawn, PieceRayVolumeDb);
			if (!IsInsideTree()) return;
			await ToSignal(GetTree().CreateTimer(PieceGrantPause), SceneTreeTimer.SignalName.Timeout);
			if (!IsInsideTree()) return;

			// Remaining pieces in chunks of 4 (one player at a time, all concurrent).
			float chunkWait = ItemRayGrowTime + ItemRayHoldTime + ItemRayShrinkTime + PieceGrantPause * 2f;
			for (int i = 1; i < pieceGrants.Length; i += 4)
			{
				int end = Math.Min(i + 4, pieceGrants.Length);
				for (int j = i; j < end; j++)
					_ = ShootItemRay(pieceGrants[j].worldPos, pieceGrants[j].onSpawn, PieceRayVolumeDb);
				await ToSignal(GetTree().CreateTimer(chunkWait), SceneTreeTimer.SignalName.Timeout);
				if (!IsInsideTree()) return;
			}

			// Start the sword speech but don't wait — ascend and emit signal immediately so
			// the sword spawns while Ophanim is still talking.
			await ToSignal(GetTree().CreateTimer(0.3f), SceneTreeTimer.SignalName.Timeout);
			if (!IsInsideTree()) return;
			_ = RunDecodeSequenceAsync(SwordSpawnMessage, SwordSpawnHoldDuration);
		}

		EmitSignal(SignalName.InitiativeSequenceCompleted);
		AscendAndHide();
	}

	public void AscendAndHide()
	{
		if (_interactable != null) _interactable.Enabled = false;
		StopGarbledAudio();

		var tween = CreateTween();
		tween.TweenMethod(
			Callable.From((float v) => _descendYOffset = v),
			_descendYOffset, RestingOffset, DescentDuration)
			.SetTrans(Tween.TransitionType.Cubic)
			.SetEase(Tween.EaseType.In);
		tween.Finished += () => _descendYOffset = RestingOffset;
	}

	/// <summary>
	/// Fully hides Ophanim back to DescentHiddenOffset and resets activation state
	/// so it can descend again for the next match. Call from ResetGame().
	/// </summary>
	public void FullReset()
	{
		if (_interactable != null) _interactable.Enabled = false;
		StopGarbledAudio();
		DismissBubble();
		_vibrateDuration = 0f;
		_hasActivated    = false;

		var tween = CreateTween();
		tween.TweenMethod(
			Callable.From((float v) => _descendYOffset = v),
			_descendYOffset, DescentHiddenOffset, DescentDuration)
			.SetTrans(Tween.TransitionType.Cubic)
			.SetEase(Tween.EaseType.In);
		tween.Finished += () => _descendYOffset = DescentHiddenOffset;
	}

	/// <summary>
	/// Win sequence: says the phrase then builds a blinding cream-white light.
	/// Run this while flying the camera toward Ophanim and ramping the fisheye shader.
	/// Returns when the light has reached peak intensity (caller fades to white next).
	/// </summary>
	public async System.Threading.Tasks.Task WinFlashAsync()
	{
		await SayAsync("Didn't think you'd make it this far", 2.0f);
		if (!IsInsideTree()) return;

		const float buildDuration = 3.0f;
		var winLight = new OmniLight3D
		{
			LightColor  = new Color(1f, 0.97f, 0.88f), // warm cream-white
			LightEnergy = 0f,
			OmniRange   = 25f,
		};
		AddChild(winLight);
		winLight.Position = Vector3.Zero;

		winLight.CreateTween()
			.TweenProperty(winLight, "light_energy", 20f, buildDuration)
			.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);

		await ToSignal(GetTree().CreateTimer(buildDuration), SceneTreeTimer.SignalName.Timeout);
		if (IsInstanceValid(winLight)) winLight.QueueFree();
	}

	// ── Devour sequence ──────────────────────────────────────────────────────

	/// <summary>
	/// Ophanim eats a list of Node3D targets (character model + pieces).
	/// Starts vibrating with gear sounds, sucks all targets into itself while a
	/// red light swells, then plays the cling, stops vibrating, and ascends.
	/// </summary>
	public async System.Threading.Tasks.Task DevourAsync(
		System.Collections.Generic.List<Node3D> targets)
	{
		// ── Start indefinite vibration + gear audio ───────────────────────────
		_vibrateStartTime = (float)(Time.GetTicksMsec() / 1000.0);
		_vibrateDuration  = 9999f; // stopped manually below

		AudioStreamPlayer3D gearAudio = null;
		var gearClip = GD.Load<AudioStream>("res://Assets/Sound FX/gear_vibrating.wav");
		if (gearClip != null)
		{
			gearAudio = new AudioStreamPlayer3D { Stream = gearClip, VolumeDb = -4f };
			AddChild(gearAudio);
			gearAudio.Play();
		}

		// ── Spawn a growing red light (parented so it follows Ophanim) ────────
		var devourLight = new OmniLight3D
		{
			LightColor  = new Color(0.9f, 0.06f, 0.02f),
			LightEnergy = 0f,
			OmniRange   = 7f,
		};
		AddChild(devourLight);
		devourLight.Position = Vector3.Zero;

		// ── Tween all targets toward Ophanim's world centre ───────────────────
		const float eatDuration = 1.8f;
		var tinyScale = new Vector3(0.001f, 0.001f, 0.001f);
		Vector3 eatTarget = GlobalPosition;

		foreach (var node in targets)
		{
			if (!IsInstanceValid(node)) continue;
			var tw = node.CreateTween().SetParallel(true);
			tw.TweenProperty(node, "global_position", eatTarget, eatDuration)
			  .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
			tw.TweenProperty(node, "scale", tinyScale, eatDuration * 0.85f)
			  .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
		}

		// Light builds alongside the eat
		var lightGrow = devourLight.CreateTween();
		lightGrow.TweenProperty(devourLight, "light_energy", 5f, eatDuration)
				 .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);

		await ToSignal(GetTree().CreateTimer(eatDuration), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) return;

		// ── Targets have arrived — hide them ─────────────────────────────────
		foreach (var node in targets)
			if (IsInstanceValid(node)) node.Visible = false;

		// ── Stop vibration + cling ────────────────────────────────────────────
		_vibrateDuration = 0f;

		var clingClip = GD.Load<AudioStream>("res://Assets/Sound FX/success_cling.wav");
		if (clingClip != null)
		{
			var cling = new AudioStreamPlayer3D { Stream = clingClip, VolumeDb = -2f };
			AddChild(cling);
			cling.Play();
			cling.Finished += () => { if (IsInstanceValid(cling)) cling.QueueFree(); };
		}

		// Fade gear audio out
		const float gearFade = 0.8f;
		if (IsInstanceValid(gearAudio))
		{
			gearAudio.CreateTween().TweenProperty(gearAudio, "volume_db", -80f, gearFade);
		}

		// Shrink and free the light
		if (IsInstanceValid(devourLight))
		{
			devourLight.CreateTween()
					   .TweenProperty(devourLight, "light_energy", 0f, 0.7f);
		}

		await ToSignal(GetTree().CreateTimer(gearFade), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) return;

		if (IsInstanceValid(gearAudio))  { gearAudio.Stop();  gearAudio.QueueFree(); }
		if (IsInstanceValid(devourLight)) devourLight.QueueFree();

		// ── Return to normal ─────────────────────────────────────────────────
		AscendAndHide();
	}

	// ── Gun decision prompt ───────────────────────────────────────────────────

	/// <summary>Shows a bobbing "!" and routes Ophanim interactions to GunSkipRequested until hidden.</summary>
	public void ShowGunDecisionPrompt()
	{
		_gunDecisionActive = true;
		SpawnGunDecisionLabel();
	}

	/// <summary>Removes the gun decision "!" and restores normal interaction behaviour.</summary>
	public void HideGunDecisionPrompt()
	{
		_gunDecisionActive = false;
		DespawnGunDecisionLabel();
	}

	/// <summary>Immediately hides the dialog bubble and cancels any pending hold timer.</summary>
	public void DismissBubble()
	{
		++_decodeSequenceId;
		_isDecoding = false;
		_dialogBubble?.HideDialog();
	}

	private void SpawnGunDecisionLabel()
	{
		if (_gunDecisionLabel != null && IsInstanceValid(_gunDecisionLabel)) return;
		var font = ResourceLoader.Load<Font>("res://Assets/Fonts/Jersey10-Regular.ttf");
		_gunDecisionLabel = new Label3D
		{
			Text            = "!",
			Font            = font,
			FontSize        = 72,
			PixelSize       = 0.004f,
			Billboard       = BaseMaterial3D.BillboardModeEnum.Enabled,
			NoDepthTest     = true,
			Modulate        = new Color(1f, 0.88f, 0f),
			OutlineSize     = 12,
			OutlineModulate = new Color(0f, 0f, 0f, 1f),
			Position        = Vector3.Up * 0.40f,
		};
		AddChild(_gunDecisionLabel);
		var bob = _gunDecisionLabel.CreateTween().SetLoops();
		bob.TweenProperty(_gunDecisionLabel, "position:y", 0.47f, 0.5f)
		   .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		bob.TweenProperty(_gunDecisionLabel, "position:y", 0.33f, 0.5f)
		   .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
	}

	private void DespawnGunDecisionLabel()
	{
		if (_gunDecisionLabel != null && IsInstanceValid(_gunDecisionLabel))
			_gunDecisionLabel.QueueFree();
		_gunDecisionLabel = null;
	}

	/// <summary>Fires a red life-drain ray from Ophanim toward the given world position (fire and forget).</summary>
	public void ShootLifeLostRay(Vector3 targetWorldPos)
	{
		_ = ShootItemRay(targetWorldPos, null, BrimstoneRayVolumeDb, new Color(0.85f, 0.08f, 0.08f));
	}

	/// <summary>Fires a small white/cream reroll ray from Ophanim toward the given world position (fire and forget).
	/// Used by TableManager after the cigarette vibration sequence to signal the new dice result.</summary>
	public void ShootRerollRay(Vector3 targetWorldPos)
	{
		_ = ShootItemRay(targetWorldPos, null, PieceRayVolumeDb, new Color(0.95f, 0.9f, 0.7f));
	}

	private async System.Threading.Tasks.Task ShootItemRay(Vector3 targetWorldPos, System.Action onSpawn, float volumeDb = float.NaN, Color? tintColor = null)
	{
		Vector3 from = GlobalPosition;
		Vector3 to   = targetWorldPos;
		Vector3 dir  = to - from;
		float   dist = dir.Length();
		if (dist < 0.01f) { onSpawn?.Invoke(); return; }
		dir = dir.Normalized();

		// Build orientation: local Y → direction of ray.
		Vector3 refUp = Mathf.Abs(dir.Dot(Vector3.Up)) < 0.9f ? Vector3.Up : Vector3.Right;
		Vector3 right = dir.Cross(refUp).Normalized();
		Vector3 back  = right.Cross(dir).Normalized();

		Color rayColor = tintColor ?? new Color(1f, 1f, 1f);

		// Ray mesh — emissive cylinder.
		var rayNode = new MeshInstance3D { TopLevel = true };
		GetParent().AddChild(rayNode);
		rayNode.GlobalTransform = new Transform3D(new Basis(right, dir, back), (from + to) * 0.5f);

		var cylinder = new CylinderMesh
		{
			Height       = dist,
			TopRadius    = ItemRayRadius,
			BottomRadius = ItemRayRadius,
		};
		var mat = new StandardMaterial3D
		{
			ShadingMode             = StandardMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor             = rayColor,
			EmissionEnabled         = true,
			Emission                = rayColor,
			EmissionEnergyMultiplier = 48f,
		};
		rayNode.Mesh             = cylinder;
		rayNode.MaterialOverride = mat;

		// Play brimstone sound.
		AudioStreamPlayer3D audio = null;
		if (BrimstoneRaySound != null)
		{
			audio            = new AudioStreamPlayer3D { TopLevel = true };
			audio.Stream     = BrimstoneRaySound;
			audio.VolumeDb   = float.IsNaN(volumeDb) ? BrimstoneRayVolumeDb : volumeDb;
			GetParent().AddChild(audio);
			audio.GlobalPosition = from;
			audio.Play();
		}

		// Grow from needle-thin to full width.
		rayNode.Scale = new Vector3(0f, 1f, 0f);
		var growTween = CreateTween().SetParallel()
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.Out);
		growTween.TweenProperty(rayNode, "scale:x", 1f, ItemRayGrowTime);
		growTween.TweenProperty(rayNode, "scale:z", 1f, ItemRayGrowTime);
		await ToSignal(growTween, Tween.SignalName.Finished);
		if (!IsInsideTree()) { rayNode.QueueFree(); audio?.QueueFree(); return; }

		// Hold.
		await ToSignal(GetTree().CreateTimer(ItemRayHoldTime), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) { rayNode.QueueFree(); audio?.QueueFree(); return; }

		// Spawn the item.
		onSpawn?.Invoke();

		// Collapse the ray.
		var shrinkTween = CreateTween().SetParallel()
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.In);
		shrinkTween.TweenProperty(rayNode, "scale:x", 0f, ItemRayShrinkTime);
		shrinkTween.TweenProperty(rayNode, "scale:z", 0f, ItemRayShrinkTime);
		await ToSignal(shrinkTween, Tween.SignalName.Finished);

		rayNode.QueueFree();
		// Let the audio finish naturally rather than cutting it.
		if (audio != null)
			audio.Finished += () => { if (IsInstanceValid(audio)) audio.QueueFree(); };
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
		int myId = ++_decodeSequenceId;
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

		// Only the most recently started decode may hide the bubble and clear the flag.
		// A stale fire-and-forget (e.g. GreetingMsg3) must not overwrite a newer sequence.
		if (_decodeSequenceId == myId)
		{
			_dialogBubble?.HideDialog();
			_isDecoding = false;
		}
	}

	public override void _ExitTree()
	{
		FocusController.FocusStateChanged -= OnFocusStateChanged;
	}

	private void OnFocusStateChanged(bool focused)
	{
		Visible = !focused;
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

	private static void SetPhysicsEnabled(Node node, bool enabled)
	{
		if (node is CollisionObject3D col)
			col.ProcessMode = enabled ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
		foreach (Node child in node.GetChildren(true))
			SetPhysicsEnabled(child, enabled);
	}
}
