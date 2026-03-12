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

		// Start ambient hellfire glow on clipboard UIs
		StartAmbientFireOnClipboards();
	}

	public void HideClipboards()
	{
		if (!_clipboardsVisible) return;
		_clipboardsVisible = false;

		// Stop ambient fire before burn-out
		StopAmbientFireOnClipboards();

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
		AddSmoke(clipboard);
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
		flash.OmniRange = 6.0f;
		
		target.GetParent().AddChild(flash);
		flash.GlobalPosition = target.GlobalPosition;

		Tween flashTween = CreateTween();
		flashTween.TweenProperty(flash, "light_energy", 5.0f, TransitionDuration * 0.15f);
		flashTween.TweenProperty(flash, "light_energy", 0.0f, TransitionDuration * 0.85f);
		flashTween.Finished += () => flash.QueueFree();
	}

	private void AddEmbers(Node3D target)
	{
		if (target == null || target.GetParent() == null) return;

		// Capture position before any scale tween might affect it
		Vector3 spawnPos = target.GlobalPosition;

		CpuParticles3D particles = new CpuParticles3D();
		particles.TopLevel = true; // Prevent parent scale from collapsing particles
		target.GetParent().AddChild(particles);
		particles.GlobalPosition = spawnPos;
		
		particles.Amount = 120;
		particles.Lifetime = TransitionDuration * 1.5f;
		particles.OneShot = true;
		particles.Explosiveness = 0.85f;
		
		particles.EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere;
		particles.EmissionSphereRadius = 0.15f;
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

	private void AddSmoke(Node3D target)
	{
		if (target == null || target.GetParent() == null) return;

		// Capture position before any scale tween might affect it
		Vector3 spawnPos = target.GlobalPosition;

		CpuParticles3D smoke = new CpuParticles3D();
		smoke.TopLevel = true; // Prevent parent scale from collapsing particles
		target.GetParent().AddChild(smoke);
		smoke.GlobalPosition = spawnPos;

		smoke.Amount = 30;
		smoke.Lifetime = TransitionDuration * 2.5f;
		smoke.OneShot = true;
		smoke.Explosiveness = 0.5f;

		smoke.EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere;
		smoke.EmissionSphereRadius = 0.12f;
		smoke.Direction = new Vector3(0, 1, 0);
		smoke.Spread = 35.0f;
		smoke.Gravity = new Vector3(0, 0.5f, 0);
		smoke.InitialVelocityMin = 0.5f;
		smoke.InitialVelocityMax = 0.8f;
		smoke.ScaleAmountMin = 1.0f;
		smoke.ScaleAmountMax = 3.0f;

		Gradient smokeGradient = new Gradient();
		smokeGradient.SetColor(0, new Color(0.15f, 0.1f, 0.08f, 0.4f));
		smokeGradient.SetColor(1, new Color(0.1f, 0.08f, 0.06f, 0.0f));
		smoke.ColorRamp = smokeGradient;

		QuadMesh smokeQuad = new QuadMesh();
		smokeQuad.Size = new Vector2(0.06f, 0.06f);
		smoke.Mesh = smokeQuad;

		StandardMaterial3D smokeMat = new StandardMaterial3D();
		smokeMat.ShadingMode = StandardMaterial3D.ShadingModeEnum.Unshaded;
		smokeMat.VertexColorUseAsAlbedo = true;
		smokeMat.BillboardMode = StandardMaterial3D.BillboardModeEnum.Enabled;
		smokeMat.Transparency = StandardMaterial3D.TransparencyEnum.Alpha;
		smoke.MaterialOverride = smokeMat;

		smoke.Emitting = true;
		GetTree().CreateTimer(smoke.Lifetime + 1.0f).Timeout += () => smoke.QueueFree();
	}

	private void StartAmbientFireOnClipboards()
	{
		if (SignInClipboard != null)
		{
			var ui = SignInViewport?.GetNodeOrNull<ClipboardUI>("ClipboardUI");
			ui?.StartAmbientFire();
		}
		if (RegistrationClipboard != null)
		{
			var ui = RegistrationViewport?.GetNodeOrNull<ClipboardUI>("ClipboardUI");
			ui?.StartAmbientFire();
		}
	}

	private void StopAmbientFireOnClipboards()
	{
		if (SignInViewport != null)
		{
			var ui = SignInViewport.GetNodeOrNull<ClipboardUI>("ClipboardUI");
			ui?.StopAmbientFire();
		}
		if (RegistrationViewport != null)
		{
			var ui = RegistrationViewport.GetNodeOrNull<ClipboardUI>("ClipboardUI");
			ui?.StopAmbientFire();
		}
	}
}
