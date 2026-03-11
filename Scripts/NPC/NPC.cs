using Godot;
using System;

public partial class NPC : CharacterBody3D
{
	[Export] public string IdleAnimation = "Armature|mixamo_com|Layer0_007";
	[Export] public float TransitionDuration = 0.8f;
	[Export] public Color TransitionColor = new Color(1.0f, 0.5f, 0.2f); // Orange flash
	
	private AnimationPlayer _animPlayer;
	private Node3D _model;
	private AudioStreamPlayer3D _burnAudio;

	public override void _Ready()
	{
		_model = GetNodeOrNull<Node3D>("OrientationFix");
		_animPlayer = GetNodeOrNull<AnimationPlayer>("OrientationFix/pigga/AnimationPlayer");
		if (_animPlayer == null) _animPlayer = FindAnimationPlayer(this);
		_burnAudio = GetNodeOrNull<AudioStreamPlayer3D>("BurnAudio");

		GD.Print($"[NPC] Ready. Model: {(_model != null ? _model.Name : "Null")}, AnimPlayer: {(_animPlayer != null ? _animPlayer.Name : "Null")}");

		if (_animPlayer != null && _animPlayer.HasAnimation(IdleAnimation))
		{
			_animPlayer.Play(IdleAnimation);
		}

		if (_model != null)
		{
			_model.Scale = new Vector3(0.001f, 0.001f, 0.001f);
			_model.Visible = false;
		}

		var interactable = GetNodeOrNull<Interactable>("Interactable");
		if (interactable != null)
		{
			interactable.Interacted += OnInteracted;
		}
	}

	public void Appear()
	{
		GD.Print($"[NPC] Appear() triggered. Model: {(_model != null ? _model.Name : "Null")}");
		if (_model == null || _model.Visible) return;
		
		_model.Visible = true;

		Tween tween = CreateTween();
		tween.TweenProperty(_model, "scale", new Vector3(0.38f, 0.38f, 0.38f), TransitionDuration)
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.InOut);
		
		AddBurnFlash();
		AddEmbers();

		if (_burnAudio != null)
		{
			_burnAudio.Play();
		}
	}

	private void AddBurnFlash()
	{
		OmniLight3D flash = new OmniLight3D();
		flash.LightColor = TransitionColor;
		flash.LightEnergy = 0.0f;
		flash.OmniRange = 4.0f;
		
		AddChild(flash);
		flash.GlobalPosition = GlobalPosition + Vector3.Up * 1.0f;

		Tween flashTween = CreateTween();
		flashTween.TweenProperty(flash, "light_energy", 3.0f, TransitionDuration * 0.2f);
		flashTween.TweenProperty(flash, "light_energy", 0.0f, TransitionDuration * 0.8f);
		flashTween.Finished += () => flash.QueueFree();
	}

	private void AddEmbers()
	{
		CpuParticles3D particles = new CpuParticles3D();
		AddChild(particles);
		particles.GlobalPosition = GlobalPosition + Vector3.Up * 0.5f;
		
		particles.Amount = 50;
		particles.Lifetime = TransitionDuration;
		particles.OneShot = true;
		particles.Explosiveness = 0.8f;
		
		particles.EmissionShape = CpuParticles3D.EmissionShapeEnum.Box;
		particles.EmissionBoxExtents = new Vector3(0.5f, 0.8f, 0.5f);
		particles.Direction = new Vector3(0, 1, 0);
		particles.Spread = 45.0f;
		particles.Gravity = new Vector3(0, 2.0f, 0); 
		particles.InitialVelocityMin = 0.5f;
		particles.InitialVelocityMax = 2.0f;
		
		Gradient gradient = new Gradient();
		gradient.SetColor(0, new Color(1, 1, 0.5f)); 
		gradient.SetColor(1, new Color(1, 0.2f, 0, 0)); 
		particles.ColorRamp = gradient;
		
		QuadMesh qm = new QuadMesh();
		qm.Size = new Vector2(0.015f, 0.015f);
		particles.Mesh = qm;
		
		StandardMaterial3D mat = new StandardMaterial3D();
		mat.ShadingMode = StandardMaterial3D.ShadingModeEnum.Unshaded;
		mat.VertexColorUseAsAlbedo = true; 
		mat.BillboardMode = StandardMaterial3D.BillboardModeEnum.Enabled;
		particles.MaterialOverride = mat;
		
		particles.Emitting = true;
		GetTree().CreateTimer(particles.Lifetime + 0.5f).Timeout += () => particles.QueueFree();
	}

	private void OnInteracted()
	{
		GD.Print("[NPC] Interacted with Pigga!");
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
