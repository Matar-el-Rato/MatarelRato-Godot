using Godot;
using System;
using System.Collections.Generic;

public partial class ClipboardController : Node
{
	[Export] public Interactable SignInInteractable;
	[Export] public Node3D RegistrationClipboard;
	[Export] public AudioStreamPlayer3D BurnAudio;
	
	[Export] public float TransitionDuration = 0.8f;
	[Export] public Color TransitionColor = new Color(1.0f, 0.5f, 0.2f); // Orange flash
	
	private bool _isRegistrationVisible = true;
	private Tween _transitionTween;

	public override void _Ready()
	{
		CallDeferred(MethodName.InitializeController);
	}

	private void InitializeController()
	{
		var parent = GetParent();
		SignInInteractable = parent.GetNodeOrNull<Interactable>("SignIn Clipboard/Interactable");
		RegistrationClipboard = parent.GetNodeOrNull<Node3D>("Registration Clipboard");
		BurnAudio = parent.GetNodeOrNull<AudioStreamPlayer3D>("BurnAudioPlayer");

		// Initialize state: SignIn starts visible, Registration starts hidden
		if (RegistrationClipboard != null)
		{
			RegistrationClipboard.Visible = false;
			RegistrationClipboard.Scale = Vector3.Zero;
			_isRegistrationVisible = false;
		}

		if (SignInInteractable != null)
		{
			SignInInteractable.Interacted += OnSignInInteracted;
			var sib = SignInInteractable.GetParent<Node3D>();
			if (sib != null)
			{
				sib.Visible = true;
				sib.Scale = Vector3.One;
			}
		}
	}

	private void OnSignInInteracted()
	{
		ToggleRegistration();
	}

	public void ToggleRegistration()
	{
		if (RegistrationClipboard == null) return;

		if (_transitionTween != null) _transitionTween.Kill();
		_transitionTween = CreateTween();
		
		Vector3 startScale = _isRegistrationVisible ? Vector3.One : Vector3.Zero;
		Vector3 targetScale = _isRegistrationVisible ? Vector3.Zero : Vector3.One;
		
		// Ensure starting state
		RegistrationClipboard.Scale = startScale;
		if (!_isRegistrationVisible) RegistrationClipboard.Visible = true;

		// Smooth scale tween
		_transitionTween.TweenProperty(RegistrationClipboard, "scale", targetScale, TransitionDuration)
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.InOut);

		// Add visual "burn" cues
		AddBurnFlash();
		AddEmbers();

		_transitionTween.Finished += () => {
			if (_isRegistrationVisible)
			{
				RegistrationClipboard.Visible = false;
				_isRegistrationVisible = false;
			}
			else
			{
				_isRegistrationVisible = true;
			}
		};
		
		if (BurnAudio != null)
		{
			BurnAudio.GlobalPosition = RegistrationClipboard.GlobalPosition;
			BurnAudio.Play();
		}
	}

	private void AddBurnFlash()
	{
		if (RegistrationClipboard == null || RegistrationClipboard.GetParent() == null) return;
		
		OmniLight3D flash = new OmniLight3D();
		flash.LightColor = TransitionColor;
		flash.LightEnergy = 0.0f;
		flash.OmniRange = 4.0f;
		
		// Add to parent instead of clipboard so it's not scaled to 0
		RegistrationClipboard.GetParent().AddChild(flash);
		flash.GlobalPosition = RegistrationClipboard.GlobalPosition;

		Tween flashTween = CreateTween();
		flashTween.TweenProperty(flash, "light_energy", 3.0f, TransitionDuration * 0.2f);
		flashTween.TweenProperty(flash, "light_energy", 0.0f, TransitionDuration * 0.8f);
		flashTween.Finished += () => flash.QueueFree();
	}

	private void AddEmbers()
	{
		if (RegistrationClipboard == null || RegistrationClipboard.GetParent() == null) return;

		CpuParticles3D particles = new CpuParticles3D();
		
		// Add to parent instead of clipboard so it's not scaled to 0
		RegistrationClipboard.GetParent().AddChild(particles);
		particles.GlobalPosition = RegistrationClipboard.GlobalPosition;
		
		particles.Amount = 50;
		particles.Lifetime = TransitionDuration;
		particles.OneShot = true;
		particles.Explosiveness = 0.8f;
		
		// Spawn from origin (Point shape)
		particles.EmissionShape = CpuParticles3D.EmissionShapeEnum.Point;
		
		particles.Direction = new Vector3(0, 1, 0);
		particles.Spread = 45.0f;
		particles.Gravity = new Vector3(0, 2.0f, 0); // Rise up
		particles.InitialVelocityMin = 0.5f;
		particles.InitialVelocityMax = 2.0f;
		
		// Color Variety: Gradient from Yellow to Red-Orange
		Gradient gradient = new Gradient();
		gradient.SetColor(0, new Color(1, 1, 0.5f)); // Yellowish
		gradient.SetColor(1, new Color(1, 0.2f, 0, 0)); // Fade out to red
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
}
