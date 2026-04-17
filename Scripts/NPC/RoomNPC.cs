// ═══════════════════════════════════════════════════
// RoomNPC.cs
// Standalone Pigga NPC for private rooms.
// Appears on player proximity, interactable for quirky "Wanna join?" dialogue.
// Entirely independent of the auth system and the pizzeria NPC.
// ═══════════════════════════════════════════════════
using Godot;
using System;

/// <summary>
/// Lightweight room NPC that scales in when triggered via <see cref="Appear"/>,
/// shows a random quirky phrase when interacted with, and plays the pointing animation.
/// No auth connection — completely standalone.
/// </summary>
public partial class RoomNPC : CharacterBody3D
{
	[Export] public string IdleAnimation     = "Armature|mixamo_com|Layer0_007";
	[Export] public string TalkAnimation     = "Armature_002|mixamo_com|Layer0_001";
	[Export] public float  TransitionDuration = 0.8f;
	[Export] public Color  TransitionColor    = new Color(1.0f, 0.5f, 0.2f);

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

	private AnimationPlayer     _animPlayer;
	private DialogBubble        _dialogBubble;
	private Interactable        _interactable;
	private AudioStreamPlayer3D _burnAudio;
	private readonly Random     _rng = new Random();

	private bool _hasAppeared   = false;
	private bool _hasInteracted = false;

	public bool HasAppeared => _hasAppeared;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_animPlayer   = GetNodeOrNull<AnimationPlayer>("OrientationFix/pigga/AnimationPlayer");
		if (_animPlayer == null) _animPlayer = FindAnimationPlayer(this);
		_burnAudio    = GetNodeOrNull<AudioStreamPlayer3D>("BurnAudio");
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
			_interactable.PromptText = "Talk";
			_interactable.Interacted += OnInteracted;
		}
	}

	// ── Appear / Disappear ────────────────────────────────────────────────────

	/// <summary>Scales the NPC in from near-zero with burn VFX. Idempotent.</summary>
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

	/// <summary>Shrinks the NPC back to near-zero with burn VFX, then hides it.</summary>
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

	// ── Interaction ───────────────────────────────────────────────────────────

	private async void OnInteracted()
	{
		if (!_hasAppeared || _hasInteracted) return;
		_hasInteracted = true;

		string phrase = TalkPhrases[_rng.Next(TalkPhrases.Length)];
		_dialogBubble?.ShowDialog(phrase, force: true);

		if (_animPlayer != null && _animPlayer.HasAnimation(TalkAnimation))
		{
			_animPlayer.Play(TalkAnimation, 0.3f);
			await ToSignal(_animPlayer, AnimationPlayer.SignalName.AnimationFinished);
			if (!IsInsideTree()) return;
			_animPlayer.Play(IdleAnimation, 0.3f);
		}

		_hasInteracted = false;
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
