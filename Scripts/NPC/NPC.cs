using Godot;
using System;

public partial class NPC : CharacterBody3D
{
	[Export] public string IdleAnimation = "Armature|mixamo_com|Layer0_007";
	[Export] public float TransitionDuration = 0.8f;
	[Export] public Color TransitionColor = new Color(1.0f, 0.5f, 0.2f); // Orange flash
	[Export] public string DialogText = "Good to see you again...\n what can i get for you today?";
	[Export] public NodePath DialogBubblePath;
	
	private AnimationPlayer _animPlayer;
	private Node3D _model;
	private AudioStreamPlayer3D _burnAudio;
	private DialogBubble _dialogBubble;
	private bool _hasAppeared = false;
	private bool _hasInteracted = false;

	public override void _Ready()
	{
		_model = GetNodeOrNull<Node3D>("OrientationFix");
		_animPlayer = GetNodeOrNull<AnimationPlayer>("OrientationFix/pigga/AnimationPlayer");
		if (_animPlayer == null) _animPlayer = FindAnimationPlayer(this);
		_burnAudio = GetNodeOrNull<AudioStreamPlayer3D>("BurnAudio");

		if (_animPlayer != null && _animPlayer.HasAnimation(IdleAnimation))
		{
			var anim = _animPlayer.GetAnimation(IdleAnimation);
			anim.LoopMode = Animation.LoopModeEnum.Linear;
			_animPlayer.Play(IdleAnimation);
		}

		// Hide the entire NPC (root CharacterBody3D), not just the model
		Visible = false;
		Scale = new Vector3(0.001f, 0.001f, 0.001f);

		if (DialogBubblePath != null && !DialogBubblePath.IsEmpty)
		{
			_dialogBubble = GetNodeOrNull<DialogBubble>(DialogBubblePath);
		}
		else
		{
			_dialogBubble = GetNodeOrNull<DialogBubble>("DialogBubble");
		}

		var interactable = GetNodeOrNull<Interactable>("Interactable");
		if (interactable != null)
		{
			interactable.Interacted += OnInteracted;
		}
	}

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

		if (_burnAudio != null)
		{
			_burnAudio.Play();
		}
	}

	private void AddBurnFlash()
	{
		OmniLight3D flash = new OmniLight3D();
		flash.TopLevel = true;
		flash.LightColor = TransitionColor;
		flash.LightEnergy = 0.0f;
		flash.OmniRange = 6.0f;
		
		Vector3 spawnPos = GlobalPosition + Vector3.Up * 1.0f;
		GetParent().AddChild(flash);
		flash.GlobalPosition = spawnPos;

		Tween flashTween = CreateTween();
		flashTween.TweenProperty(flash, "light_energy", 5.0f, TransitionDuration * 0.15f);
		flashTween.TweenProperty(flash, "light_energy", 0.0f, TransitionDuration * 0.85f);
		flashTween.Finished += () => flash.QueueFree();
	}

	private void AddEmbers()
	{
		Vector3 spawnPos = GlobalPosition + Vector3.Up * 0.5f;

		CpuParticles3D particles = new CpuParticles3D();
		particles.TopLevel = true;
		GetParent().AddChild(particles);
		particles.GlobalPosition = spawnPos;
		
		particles.Amount = 120;
		particles.Lifetime = TransitionDuration * 1.5f;
		particles.OneShot = true;
		particles.Explosiveness = 0.85f;
		
		particles.EmissionShape = CpuParticles3D.EmissionShapeEnum.Box;
		particles.EmissionBoxExtents = new Vector3(0.5f, 0.8f, 0.5f);
		particles.Direction = new Vector3(0, 1, 0);
		particles.Spread = 55.0f;
		particles.Gravity = new Vector3(0, 2.0f, 0); 
		particles.InitialVelocityMin = 1.0f;
		particles.InitialVelocityMax = 2.5f;
		particles.ScaleAmountMin = 0.8f;
		particles.ScaleAmountMax = 1.5f;
		
		Gradient gradient = new Gradient();
		gradient.SetColor(0, new Color(1, 1, 0.5f, 1)); 
		gradient.AddPoint(0.3f, new Color(1, 0.5f, 0.1f, 0.9f));
		gradient.SetColor(gradient.GetPointCount() - 1, new Color(0.8f, 0.1f, 0, 0)); 
		particles.ColorRamp = gradient;
		
		QuadMesh qm = new QuadMesh();
		qm.Size = new Vector2(0.018f, 0.018f);
		particles.Mesh = qm;
		
		StandardMaterial3D mat = new StandardMaterial3D();
		mat.ShadingMode = StandardMaterial3D.ShadingModeEnum.Unshaded;
		mat.VertexColorUseAsAlbedo = true; 
		mat.BillboardMode = StandardMaterial3D.BillboardModeEnum.Enabled;
		mat.Transparency = StandardMaterial3D.TransparencyEnum.Alpha;
		particles.MaterialOverride = mat;
		
		particles.Emitting = true;
		GetTree().CreateTimer(particles.Lifetime + 0.5f).Timeout += () => particles.QueueFree();
	}

	private async void OnInteracted()
	{
		if (!_hasAppeared || _hasInteracted) return;
		_hasInteracted = true;

		GD.Print("NPC Interacted, showing dialog...");

		if (_dialogBubble != null)
		{
			_dialogBubble.ShowDialog(DialogText);
		}

		// Wait 1 second after interacting
		await ToSignal(GetTree().CreateTimer(1.0f), SceneTreeTimer.SignalName.Timeout);

		GD.Print("Spawning clipboards...");
		if (ClipboardController.Instance != null)
		{
			ClipboardController.Instance.ShowClipboards();
		}
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
