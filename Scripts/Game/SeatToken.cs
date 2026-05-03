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

	public override void _Ready()
	{
		Visible = false;
		Scale   = new Vector3(0.001f, 0.001f, 0.001f);
	}

	// ── Public API ────────────────────────────────────────────────────────────

	/// <summary>
	/// Sets username label and spawns the character model at the sitting position.
	/// Call before Appear(). Token must already be positioned at chair.GlobalPosition
	/// with chair.GlobalRotation — sitting offset is applied internally.
	/// </summary>
	public void SetPlayerInfo(string username, string colorKey, int skinId, Chair chair)
	{
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
		AddBurnFlash();
		AddEmbers();
		var tween = CreateTween();
		tween.TweenProperty(this, "scale", new Vector3(0.001f, 0.001f, 0.001f), TransitionDuration)
			 .SetTrans(Tween.TransitionType.Quad)
			 .SetEase(Tween.EaseType.InOut);
		tween.Finished += () => { if (IsInsideTree()) QueueFree(); };
	}

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
		// Scale and rotation must both match OrientationFix (0.38 uniform + -90° Y).
		var container = new Node3D();
		container.Position = chair.SitOffset + entry.SittingOffset;
		if (orientFix != null)
		{
			container.Rotation = orientFix.Rotation;
			container.Scale    = orientFix.Scale;
		}
		AddChild(container);

		// Instantiate the character model under the orientation container.
		var model = entry.ModelScene.Instantiate<Node3D>();
		container.AddChild(model);

		// Find AnimationPlayer (direct child first, then recursive search).
		var animPlayer = model.GetNodeOrNull<AnimationPlayer>("AnimationPlayer")
					  ?? model.FindChild("AnimationPlayer", true, false) as AnimationPlayer;

		if (animPlayer != null)
		{
			// Apply idle rotation to root bone (compensates Blender export offset).
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
		// Container origin is at (SitOffset + SittingOffset) local to the token.
		// Seated head is roughly 1.5 units above the container origin.
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
