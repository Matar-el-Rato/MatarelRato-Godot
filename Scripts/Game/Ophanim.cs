using Godot;
using System;
using System.Collections.Generic;

public partial class Ophanim : Node3D
{
	// ── Animation ─────────────────────────────────────────────────────────────
	[Export] public float AnimationSpeed = 1.75f;

	// ── Hover ─────────────────────────────────────────────────────────────────
	[ExportGroup("Hover")]
	[Export] public float HoverBobAmplitude    = 0.12f;
	[Export] public float HoverSwayAmplitude   = 0.05f;
	[Export] public float HoverRotateDeg       = 1.5f;
	[Export] public float HoverSpeed           = 0.7f;

	// ── Decode Settings ───────────────────────────────────────────────────────
	[ExportGroup("Decode")]
	[Export] public string DecodeText            = "[SUBJECT:P1] IS GOING TO DIE.";
	[Export] public float  GrowthInterval         = 0.01f;
	[Export] public float  ScrambleStartFraction  = 0.2f;   // fraction of chars grown before scramble begins
	[Export] public float  Phase2Duration         = 0.5f;
	[Export] public float  Phase2FlipsPerSec      = 14f;
	[Export] public float  Phase3StaggerInterval  = 0.06f;
	[Export] public float  RedactionRate         = 0.1f;
	[Export] public float  HoldDuration          = 5.0f;
	[Export] public int    BubbleViewportWidth   = 900;

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
	private DialogBubble        _dialogBubble;
	private Interactable        _interactable;
	private Tween               _flickerTween;
	private bool                _isDecoding;
	private bool                _garbledShouldLoop;

	private readonly Random _rng = new Random();
	private Vector3 _basePosition;
	private Vector3 _baseRotation;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_basePosition = Position;
		_baseRotation = Rotation;
		var animPlayer = FindAnimationPlayer(this);
		if (animPlayer != null && animPlayer.HasAnimation("Animation"))
		{
			var anim = animPlayer.GetAnimation("Animation");
			anim.LoopMode = Animation.LoopModeEnum.Linear;
			animPlayer.SpeedScale = AnimationSpeed;
			animPlayer.Play("Animation");
		}

		SetupAmbientAudio("DroneAudio");
		SetupAmbientAudio("WingFlapAudio");

		_garbledAudio = GetNodeOrNull<AudioStreamPlayer3D>("GarbledSpeech");
		_dialogBubble = GetNodeOrNull<DialogBubble>("DialogBubble");
		_interactable = GetNodeOrNull<Interactable>("Interactable");

		if (_garbledAudio != null)
			_garbledAudio.Finished += OnGarbledFinished;

		if (_interactable != null)
			_interactable.Interacted += OnInteracted;

		// Widen the dialog viewport so long decode text doesn't get clipped.
		var viewport = _dialogBubble?.GetNodeOrNull<SubViewport>("SubViewport");
		if (viewport != null)
			viewport.Size = new Vector2I(BubbleViewportWidth, 160);
	}

	public override void _Process(double delta)
	{
		float t = (float)Time.GetTicksMsec() / 1000.0f * HoverSpeed;
		Position = _basePosition + new Vector3(
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

	private void SetupAmbientAudio(string nodeName)
	{
		var player = GetNodeOrNull<AudioStreamPlayer3D>(nodeName);
		if (player == null) return;
		player.Finished += () => player.Play();
		player.Play();
	}

	// ── Garbled audio ─────────────────────────────────────────────────────────

	private void OnGarbledFinished()
	{
		if (_garbledShouldLoop) _garbledAudio?.Play();
	}

	private void StartGarbledAudio()
	{
		if (_garbledAudio == null) return;
		_garbledShouldLoop = true;
		_garbledAudio.Play((float)(_rng.NextDouble() * 5.0));
	}

	private void StopGarbledAudio()
	{
		_garbledShouldLoop = false;
		_garbledAudio?.Stop();
	}

	// ── Interaction ───────────────────────────────────────────────────────────

	private void OnInteracted()
	{
		if (_isDecoding) return;
		RunDecodeSequence();
	}

	// ── Bubble helpers ────────────────────────────────────────────────────────

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
		popup.TweenProperty(_dialogBubble, "scale", Vector3.One, 0.3f)
			 .SetTrans(Tween.TransitionType.Cubic)
			 .SetEase(Tween.EaseType.Out);
	}

	private void UpdateBubbleText(string text)
	{
		if (_dialogBubble?.TextLabel == null) return;
		_dialogBubble.TextLabel.Text = text;
	}

	// ── Decode Sequence ───────────────────────────────────────────────────────

	private async void RunDecodeSequence()
	{
		_isDecoding = true;

		string target  = DecodeText;
		int    len     = target.Length;
		var    display  = new char[len];
		var    resolved = new bool[len];

		// Non-glitchable chars (spaces, punctuation) are pre-resolved.
		for (int i = 0; i < len; i++)
		{
			display[i]  = target[i];
			resolved[i] = !ShouldGlitch(target[i]);
		}

		// Collect glitchable indices and mark ~10% as permanently redacted.
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

		// Pre-fill display: blocks for glitch chars, real value for the rest.
		for (int i = 0; i < len; i++)
			display[i] = resolved[i] ? target[i] : RandomBlock();

		// ── Phase 0: Growth — blocks appear one by one; scramble starts mid-way ─
		ShowBubble("");
		StartGarbledAudio();

		int scrambleAt = Mathf.Max(1, (int)(len * ScrambleStartFraction));

		// Flicker during the initial pure-block portion.
		if (_dialogBubble?.DisplaySprite != null)
		{
			_flickerTween = CreateTween().SetLoops();
			_flickerTween.TweenProperty(_dialogBubble.DisplaySprite, "modulate:a", 0.4f, 0.07f);
			_flickerTween.TweenProperty(_dialogBubble.DisplaySprite, "modulate:a", 1.0f, 0.10f);
		}

		for (int i = 0; i < len; i++)
		{
			// Once past the threshold: kill flicker and scramble already-placed chars.
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

		// Kill flicker in case all chars were grown before threshold was hit.
		_flickerTween?.Kill();
		_flickerTween = null;
		if (_dialogBubble?.DisplaySprite != null)
			_dialogBubble.DisplaySprite.Modulate = Colors.White;

		// ── Phase 2: Scramble — rapid symbol cycling ──────────────────────────
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

		// ── Phase 3: Resolution — chars lock left-to-right, rest keep scrambling
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

		// ── Hold final text ───────────────────────────────────────────────────
		await ToSignal(GetTree().CreateTimer(HoldDuration), SceneTreeTimer.SignalName.Timeout);
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
