using Godot;
using System;
using System.Collections.Generic;

public partial class ClipboardController : Node
{
	public static ClipboardController Instance { get; private set; }
	[Export] public Node3D SignInClipboard;
	[Export] public Node3D RegistrationClipboard;
	[Export] public AudioStreamPlayer3D BurnAudio;
	
	[Export] public float TransitionDuration = 0.8f;
	[Export] public Color TransitionColor = new Color(1.0f, 0.5f, 0.2f); // Orange flash
	
	[Export] public SubViewport SignInViewport;
	[Export] public SubViewport RegistrationViewport;

	private bool _clipboardsVisible = false;
	private Interactable _signInInteractable;
	private Interactable _registrationInteractable;

	public override void _Ready()
	{
		Instance = this;
		CallDeferred(MethodName.InitializeController);
	}

	private void InitializeController()
	{
		var parent = GetParent();
		// Find nodes if not assigned via export
		SignInClipboard ??= parent.GetNodeOrNull<Node3D>("SignIn Clipboard");
		RegistrationClipboard ??= parent.GetNodeOrNull<Node3D>("Registration Clipboard");
		BurnAudio ??= parent.GetNodeOrNull<AudioStreamPlayer3D>("BurnAudioPlayer");
		
		_signInInteractable = SignInClipboard?.GetNodeOrNull<Interactable>("Interactable");
		_registrationInteractable = RegistrationClipboard?.GetNodeOrNull<Interactable>("Interactable");

		GD.Print($"[ClipboardController] SignInInteractable: {(_signInInteractable != null ? "Found" : "Null")}");
		GD.Print($"[ClipboardController] RegistrationInteractable: {(_registrationInteractable != null ? "Found" : "Null")}");

		// Initial state: Both hidden
		if (SignInClipboard != null) HideClipboard(SignInClipboard);
		if (RegistrationClipboard != null) HideClipboard(RegistrationClipboard);
		
		_clipboardsVisible = false;
	}



	private void HideClipboard(Node3D clipboard)
	{
		if (clipboard == null) return;
		clipboard.Visible = false;
		// Use a tiny scale instead of absolute zero to avoid Jolt Physics singular transform warnings
		clipboard.Scale = new Vector3(0.001f, 0.001f, 0.001f);
	}

	public void ShowClipboards()
	{
		if (_clipboardsVisible) return;
		_clipboardsVisible = true;

		if (SignInClipboard != null) AppearClipboard(SignInClipboard);
		if (RegistrationClipboard != null) AppearClipboard(RegistrationClipboard);

		if (BurnAudio != null)
		{
			BurnAudio.GlobalPosition = SignInClipboard?.GlobalPosition ?? Vector3.Zero;
			BurnAudio.Play();
		}
	}

	public void HideClipboards()
	{
		if (!_clipboardsVisible) return;
		_clipboardsVisible = false;

		if (SignInClipboard != null) DisappearClipboard(SignInClipboard);
		if (RegistrationClipboard != null) DisappearClipboard(RegistrationClipboard);

		if (BurnAudio != null)
		{
			BurnAudio.GlobalPosition = SignInClipboard?.GlobalPosition ?? Vector3.Zero;
			BurnAudio.Play();
		}
	}

	private void DisappearClipboard(Node3D clipboard)
	{
		Tween tween = CreateTween();
		tween.TweenProperty(clipboard, "scale", new Vector3(0.001f, 0.001f, 0.001f), TransitionDuration)
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.InOut);
		
		tween.Finished += () => clipboard.Visible = false;
		
		AddBurnFlash(clipboard);
		AddEmbers(clipboard);
	}

	private void AppearClipboard(Node3D clipboard)
	{
		clipboard.Visible = true;
		Tween tween = CreateTween();
		tween.TweenProperty(clipboard, "scale", Vector3.One, TransitionDuration)
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.InOut);
		
		AddBurnFlash(clipboard);
		AddEmbers(clipboard);
	}

	private void AddBurnFlash(Node3D target)
	{
		if (target == null || target.GetParent() == null) return;
		
		OmniLight3D flash = new OmniLight3D();
		flash.LightColor = TransitionColor;
		flash.LightEnergy = 0.0f;
		flash.OmniRange = 4.0f;
		
		target.GetParent().AddChild(flash);
		flash.GlobalPosition = target.GlobalPosition;

		Tween flashTween = CreateTween();
		flashTween.TweenProperty(flash, "light_energy", 3.0f, TransitionDuration * 0.2f);
		flashTween.TweenProperty(flash, "light_energy", 0.0f, TransitionDuration * 0.8f);
		flashTween.Finished += () => flash.QueueFree();
	}

	private void AddEmbers(Node3D target)
	{
		if (target == null || target.GetParent() == null) return;

		CpuParticles3D particles = new CpuParticles3D();
		target.GetParent().AddChild(particles);
		particles.GlobalPosition = target.GlobalPosition;
		
		particles.Amount = 50;
		particles.Lifetime = TransitionDuration;
		particles.OneShot = true;
		particles.Explosiveness = 0.8f;
		
		particles.EmissionShape = CpuParticles3D.EmissionShapeEnum.Point;
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
}
