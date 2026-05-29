// ═══════════════════════════════════════════════════
// SeatToken.cs
// Spawned at a chair when a player claims it.
// Instantiates their character model in the sitting
// animation and shows a floating username label.
// ═══════════════════════════════════════════════════
using Godot;

public partial class SeatToken : Node3D
{
	[Export] public float TransitionDuration = 0.5f;
	[Export] public Color TransitionColor    = new Color(1.0f, 0.5f, 0.2f);

	private Label3D _label;

	// ── Targeting API ─────────────────────────────────────────────────────────

	/// <summary>User ID of the seated player. Set by TableManager after spawning.</summary>
	public int UserId { get; private set; } = -1;

	/// <summary>World position roughly at the seated player's head.</summary>
	public Vector3 HeadWorldPosition =>
		GlobalPosition + GlobalBasis * new Vector3(0f, _headLocalY, 0f);

	private float          _headLocalY        = 1.8f;
	private StaticBody3D   _targetBody;
	private string         _username          = "";
	private Label3D        _promptLabel;
	private Tween          _promptTween;
	private Node3D         _characterContainer;
	private static readonly Color _promptColor = new Color(1f, 0.85f, 0.2f);
	private static readonly Color _stoneColor  = new Color(0.52f, 0.50f, 0.47f);

	public override void _Ready()
	{
		Visible = false;
		Scale   = new Vector3(0.001f, 0.001f, 0.001f);
	}

	// ── Public API ────────────────────────────────────────────────────────────

	public void SetUserId(int id) => UserId = id;

	/// <summary>
	/// Sets username label and spawns the character model at the sitting position.
	/// Call before Appear(). Token must already be positioned at chair.GlobalPosition
	/// with chair.GlobalRotation — sitting offset is applied internally.
	/// </summary>
	public void SetPlayerInfo(string username, string colorKey, int skinId, Chair chair)
	{
		_username = username;

		var font = ResourceLoader.Load<Font>("res://Assets/Fonts/Jersey10-Regular.ttf");
		_label = new Label3D
		{
			Font            = font,
			FontSize        = 72,
			PixelSize       = 0.004f,
			Billboard       = BaseMaterial3D.BillboardModeEnum.Enabled,
			Modulate        = new Color(1, 1, 1, 1),
			OutlineSize     = 12,
			OutlineModulate = new Color(0, 0, 0, 1f),
			Text            = username,
		};
		AddChild(_label);

		SpawnCharacter(skinId, chair);
	}

	public void Appear()
	{
		Visible = true;
		var tween = CreateTween();
		tween.TweenProperty(this, "scale", Vector3.One, TransitionDuration)
			 .SetTrans(Tween.TransitionType.Quad)
			 .SetEase(Tween.EaseType.InOut);
		AddBurnFlash();
		AddEmbers();
	}

	public void Disappear()
	{
		SetTargetable(false);
		AddBurnFlash();
		AddEmbers();
		var tween = CreateTween();
		tween.TweenProperty(this, "scale", new Vector3(0.001f, 0.001f, 0.001f), TransitionDuration)
			 .SetTrans(Tween.TransitionType.Quad)
			 .SetEase(Tween.EaseType.InOut);
		tween.Finished += () => { if (IsInsideTree()) QueueFree(); };
	}

	/// <summary>
	/// Turns the seated character into a stone statue: greys all meshes, freezes the
	/// animation, plays petrify.wav, and leaves the token in the scene permanently.
	/// </summary>
	public void Petrify()
	{
		SetTargetable(false);
		PlayPetrifySound();
		FreezeAnimation();
		if (_characterContainer != null && IsInstanceValid(_characterContainer))
			ApplyStoneColor(_characterContainer);
		if (_label != null)
		{
			_label.Modulate = new Color(0.6f, 0.6f, 0.6f, 1f);

			var font = ResourceLoader.Load<Font>("res://Assets/Fonts/Jersey10-Regular.ttf");
			var petrifiedLabel = new Label3D
			{
				Font            = font,
				FontSize        = 44,
				PixelSize       = 0.004f,
				Billboard       = BaseMaterial3D.BillboardModeEnum.Enabled,
				Modulate        = new Color(0.65f, 0.65f, 0.65f, 1f),
				OutlineSize     = 8,
				OutlineModulate = new Color(0f, 0f, 0f, 1f),
				Text            = "(Petrified)",
				Position        = _label.Position + new Vector3(0f, 0.22f, 0f),
			};
			AddChild(petrifiedLabel);
		}
	}

	private void PlayPetrifySound()
	{
		var clip = GD.Load<AudioStream>("res://Assets/Sound FX/petrify.wav");
		if (clip == null) return;
		var sfx = new AudioStreamPlayer3D { Stream = clip, VolumeDb = 2f, MaxDistance = 20f };
		AddChild(sfx);
		sfx.Play();
		sfx.Finished += () => { if (IsInstanceValid(sfx)) sfx.QueueFree(); };
	}

	private void FreezeAnimation()
	{
		if (_characterContainer == null) return;
		var animPlayer = _characterContainer.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
		animPlayer?.Stop(false); // keep current pose, don't reset to bind
	}

	private static void ApplyStoneColor(Node node)
	{
		if (node is MeshInstance3D mesh)
		{
			int count = mesh.GetSurfaceOverrideMaterialCount();
			for (int i = 0; i < count; i++)
			{
				var existing = (mesh.GetSurfaceOverrideMaterial(i)
				             ?? mesh.Mesh?.SurfaceGetMaterial(i)) as StandardMaterial3D;
				var mat = existing != null
					? (StandardMaterial3D)existing.Duplicate()
					: new StandardMaterial3D();
				mat.AlbedoColor     = _stoneColor;
				mat.EmissionEnabled = false;
				mesh.SetSurfaceOverrideMaterial(i, mat);
			}
		}
		foreach (Node child in node.GetChildren())
			ApplyStoneColor(child);
	}

	/// <summary>
	/// Adds (or removes) the collision body that lets this seat be raycast-targeted by
	/// the Handcuffs item. Safe to call multiple times; no-ops if already active.
	/// </summary>
	public void SetTargetable(bool active)
	{
		if (active)
		{
			if (_targetBody != null) return;

			// Capsule collision centred on the torso/head area.
			// Adding a StaticBody3D to the scene tree during _process / tween callbacks
			// causes a Jolt crash. Defer it so it enters the tree at the safe deferred point.
			_targetBody = new StaticBody3D { Name = "TargetBody" };
			var shape = new CollisionShape3D();
			shape.Shape = new CapsuleShape3D { Radius = 0.45f, Height = 1.8f };
			_targetBody.AddChild(shape);
			_targetBody.Position = new Vector3(0f, _headLocalY - 0.55f, 0f);
			var bodyToAdd = _targetBody;
			Callable.From(() => { if (IsInsideTree() && IsInstanceValid(bodyToAdd)) AddChild(bodyToAdd); })
			        .CallDeferred();
		}
		else
		{
			ShowHandcuffPrompt(false);
			if (_targetBody != null)
			{
				_targetBody.QueueFree();
				_targetBody = null;
			}
		}
	}

	/// <summary>Shows or hides a "Shoot {name}" billboard prompt (gun targeting).</summary>
	public void ShowGunPrompt(bool show) => ShowActionPrompt("Shoot", show);

	/// <summary>
	/// Shows or hides a "Handcuff {name}" billboard prompt above this seat's head,
	/// used while the local player is aiming the Handcuffs item at this player.
	/// </summary>
	public void ShowHandcuffPrompt(bool show) => ShowActionPrompt("Handcuff", show);

	private void ShowActionPrompt(string action, bool show)
	{
		if (show)
		{
			if (_promptLabel == null)
			{
				var font = ResourceLoader.Load<Font>("res://Assets/Fonts/Jersey10-Regular.ttf");
				_promptLabel = new Label3D
				{
					Font            = font,
					FontSize        = 56,
					PixelSize       = 0.003f,
					Billboard       = BaseMaterial3D.BillboardModeEnum.Enabled,
					Modulate        = new Color(_promptColor.R, _promptColor.G, _promptColor.B, 0f),
					OutlineSize     = 10,
					OutlineModulate = new Color(0f, 0f, 0f, 1f),
					NoDepthTest     = true,
					Text            = $"{action} {_username}",
					Position        = new Vector3(0f, _headLocalY + 0.35f, 0f),
				};
				AddChild(_promptLabel);
			}

			// Fade in, like the standard interactable prompt.
			_promptLabel.Visible = true;
			_promptTween?.Kill();
			_promptTween = CreateTween();
			_promptTween.TweenProperty(_promptLabel, "modulate:a", 1f, 0.15f)
			            .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
		}
		else if (_promptLabel != null)
		{
			// Fade out, then hide.
			_promptTween?.Kill();
			_promptTween = CreateTween();
			_promptTween.TweenProperty(_promptLabel, "modulate:a", 0f, 0.12f)
			            .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
			_promptTween.TweenCallback(Callable.From(() =>
			{
				if (IsInstanceValid(_promptLabel)) _promptLabel.Visible = false;
			}));
		}
	}

	/// <summary>Returns true if the given physics body is this token's targeting collision.</summary>
	public bool IsHitBy(Node body) =>
		_targetBody != null && (body == _targetBody || _targetBody.IsAncestorOf(body));

	// ── Character spawning ────────────────────────────────────────────────────

	private void SpawnCharacter(int skinId, Chair chair)
	{
		// Find CharacterEntry for this skin from the local player's Selector.
		var player   = GetTree().GetFirstNodeInGroup("player") as PlayerCameraController;
		var selector = player?.GetNodeOrNull<Selector>("Selector");

		CharacterEntry entry = null;
		if (selector?.Entries != null)
			foreach (var e in selector.Entries)
				if (e.ServerId == skinId) { entry = e; break; }

		if (entry?.ModelScene == null) return;

		// Mirror OrientationFix rotation from the live player scene.
		var orientFix = player?.GetNodeOrNull<Node3D>("character/OrientationFix");

		// Container sits at the same local offset the player body would use when seated.
		var container = new Node3D();
		container.Position = chair.SitOffset + entry.SittingOffset;
		if (orientFix != null)
		{
			container.Rotation = orientFix.Rotation;
			container.Scale    = orientFix.Scale;
		}
		AddChild(container);
		_characterContainer = container;

		// Store approximate head height for targeting/handcuffs.
		_headLocalY = container.Position.Y + 1.5f;

		// Instantiate the character model under the orientation container.
		var model = entry.ModelScene.Instantiate<Node3D>();
		container.AddChild(model);

		// Find AnimationPlayer (direct child first, then recursive search).
		var animPlayer = model.GetNodeOrNull<AnimationPlayer>("AnimationPlayer")
					  ?? model.FindChild("AnimationPlayer", true, false) as AnimationPlayer;

		if (animPlayer != null)
		{
			var root = animPlayer.GetNodeOrNull<Node3D>(animPlayer.RootNode);
			if (root != null)
				root.RotationDegrees = new Vector3(root.RotationDegrees.X, entry.IdleRotation, root.RotationDegrees.Z);

			if (animPlayer.HasAnimation("sittingidle_001"))
			{
				var anim = animPlayer.GetAnimation("sittingidle_001");
				if (anim != null) anim.LoopMode = Animation.LoopModeEnum.Linear;
				animPlayer.Play("sittingidle_001");
			}
		}

		// Position label just above the seated character's head.
		if (_label != null)
			_label.Position = new Vector3(0, container.Position.Y + 1.5f, 0);
	}

	// ── VFX — identical to RoomNPC ────────────────────────────────────────────

	private void AddBurnFlash()
	{
		var flash = new OmniLight3D
		{
			TopLevel    = true,
			LightColor  = TransitionColor,
			LightEnergy = 0.0f,
			OmniRange   = 6.0f
		};
		GetParent().AddChild(flash);
		flash.GlobalPosition = GlobalPosition + Vector3.Up;

		var t = CreateTween();
		t.TweenProperty(flash, "light_energy", 5.0f, TransitionDuration * 0.15f);
		t.TweenProperty(flash, "light_energy", 0.0f, TransitionDuration * 0.85f);
		t.Finished += () => flash.QueueFree();
	}

	private void AddEmbers()
	{
		var particles = new CpuParticles3D { TopLevel = true };
		GetParent().AddChild(particles);
		particles.GlobalPosition     = GlobalPosition + Vector3.Up * 0.5f;
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

		var gradient = new Gradient();
		gradient.SetColor(0, new Color(1, 1, 0.5f, 1));
		gradient.AddPoint(0.3f, new Color(1, 0.5f, 0.1f, 0.9f));
		gradient.SetColor(gradient.GetPointCount() - 1, new Color(0.8f, 0.1f, 0, 0));
		particles.ColorRamp = gradient;

		particles.Mesh = new QuadMesh { Size = new Vector2(0.018f, 0.018f) };
		particles.MaterialOverride = new StandardMaterial3D
		{
			ShadingMode            = StandardMaterial3D.ShadingModeEnum.Unshaded,
			VertexColorUseAsAlbedo = true,
			BillboardMode          = StandardMaterial3D.BillboardModeEnum.Enabled,
			Transparency           = StandardMaterial3D.TransparencyEnum.Alpha
		};
		particles.Emitting = true;

		GetTree().CreateTimer(particles.Lifetime + 0.5f).Timeout +=
			() => { if (IsInstanceValid(particles)) particles.QueueFree(); };
	}
}
