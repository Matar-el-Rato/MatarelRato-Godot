// ═══════════════════════════════════════════════════
// ClipboardController.cs
// Singleton that manages showing and hiding the two
// physical clipboard props (Sign-In and Registration)
// with VFX burn transitions.
// ═══════════════════════════════════════════════════
using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Manages the two physical clipboard Node3D props in the scene.
/// Exposes methods to show/hide each clipboard individually or together,
/// and spawns ember + smoke VFX on every transition.
/// </summary>
public partial class ClipboardController : Node
{
	/// <summary>Global singleton — set in _Ready.</summary>
	public static ClipboardController Instance { get; private set; }

	[Export] public Node3D              SignInClipboard;
	[Export] public Node3D              RegistrationClipboard;
	[Export] public AudioStreamPlayer3D BurnAudio;
	[Export] public float               TransitionDuration = 0.8f;
	[Export] public Color               TransitionColor    = new Color(1.0f, 0.5f, 0.2f);
	[Export] public SubViewport         SignInViewport;
	[Export] public SubViewport         RegistrationViewport;

	private bool         _clipboardsVisible        = false;
	private Interactable _signInInteractable;
	private Interactable _registrationInteractable;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		Instance = this;
		// Defer initialization so all sibling nodes are in the tree.
		CallDeferred(MethodName.InitializeController);
	}

	/// <summary>
	/// Resolves node references, flags clipboard types on their UIs, and hides both.
	/// Must run deferred so scene children are ready.
	/// </summary>
	private void InitializeController()
	{
		var parent = GetParent();

		// Note: scene nodes were renamed and .tscn instances swapped during development,
		// so "RegistrationClipboard1" actually holds the Sign-In UI and vice-versa.
		// Fallback by name if not assigned via Export in the editor.
		SignInClipboard       ??= parent.GetNodeOrNull<Node3D>("RegistrationClipboard1")
								?? parent.GetNodeOrNull<Node3D>("SignIn Clipboard");
		RegistrationClipboard ??= parent.GetNodeOrNull<Node3D>("SignInClipboard1")
								?? parent.GetNodeOrNull<Node3D>("Registration Clipboard");

		BurnAudio ??= parent.GetNodeOrNull<AudioStreamPlayer3D>("BurnAudioPlayer");

		// Auto-find SubViewports inside clipboard scenes if not set in the editor.
		SignInViewport       ??= SignInClipboard?.GetNodeOrNull<SubViewport>("SubViewport");
		RegistrationViewport ??= RegistrationClipboard?.GetNodeOrNull<SubViewport>("SubViewport");

		_signInInteractable       = SignInClipboard?.GetNodeOrNull<Interactable>("Interactable");
		_registrationInteractable = RegistrationClipboard?.GetNodeOrNull<Interactable>("Interactable");

		// Tell each ClipboardUI which mode it operates in.
		var signInUI = SignInViewport?.GetNodeOrNull<ClipboardUI>("ClipboardUI");
		if (signInUI != null) signInUI.IsRegistration = false;

		var regUI = RegistrationViewport?.GetNodeOrNull<ClipboardUI>("ClipboardUI");
		if (regUI != null) regUI.IsRegistration = true;

		// Start with both clipboards hidden.
		if (SignInClipboard != null)       HideClipboard(SignInClipboard);
		if (RegistrationClipboard != null) HideClipboard(RegistrationClipboard);

		_clipboardsVisible = false;
	}

	// ── Show / Hide ───────────────────────────────────────────────────────────

	/// <summary>
	/// Shows the Sign-In clipboard with a burn VFX, resetting and lighting its UI.
	/// No-op if a clipboard is already visible.
	/// </summary>
	public void ShowLoginClipboard()
	{
		if (_clipboardsVisible) return;
		_clipboardsVisible = true;

		var ui = SignInViewport?.GetNodeOrNull<ClipboardUI>("ClipboardUI");
		ui?.Reset();
		ui?.StartAmbientFire();

		if (SignInClipboard != null) AppearClipboard(SignInClipboard);

		if (BurnAudio != null)
		{
			BurnAudio.GlobalPosition = SignInClipboard?.GlobalPosition ?? Vector3.Zero;
			BurnAudio.Play();
		}
	}

	/// <summary>
	/// Shows the Registration clipboard with a burn VFX, resetting and lighting its UI.
	/// No-op if a clipboard is already visible.
	/// </summary>
	public void ShowRegistrationClipboard()
	{
		if (_clipboardsVisible) return;
		_clipboardsVisible = true;

		var ui = RegistrationViewport?.GetNodeOrNull<ClipboardUI>("ClipboardUI");
		ui?.Reset();
		ui?.StartAmbientFire();

		if (RegistrationClipboard != null) AppearClipboard(RegistrationClipboard);

		if (BurnAudio != null)
		{
			BurnAudio.GlobalPosition = RegistrationClipboard?.GlobalPosition ?? Vector3.Zero;
			BurnAudio.Play();
		}
	}

	/// <summary>
	/// Shows both clipboards simultaneously. Used for legacy "show all" flow.
	/// No-op if already visible.
	/// </summary>
	public void ShowClipboards()
	{
		if (_clipboardsVisible) return;
		_clipboardsVisible = true;

		if (SignInClipboard != null)       AppearClipboard(SignInClipboard);
		if (RegistrationClipboard != null) AppearClipboard(RegistrationClipboard);

		if (BurnAudio != null)
		{
			BurnAudio.GlobalPosition = SignInClipboard?.GlobalPosition ?? Vector3.Zero;
			BurnAudio.Play();
		}

		StartAmbientFireOnClipboards();
	}

	/// <summary>
	/// Hides all currently visible clipboards with a burn/smoke VFX.
	/// No-op if already hidden.
	/// </summary>
	public void HideClipboards()
	{
		if (!_clipboardsVisible) return;
		_clipboardsVisible = false;

		StopAmbientFireOnClipboards();

		if (SignInClipboard != null)       DisappearClipboard(SignInClipboard);
		if (RegistrationClipboard != null) DisappearClipboard(RegistrationClipboard);

		if (BurnAudio != null)
		{
			BurnAudio.GlobalPosition = SignInClipboard?.GlobalPosition ?? Vector3.Zero;
			BurnAudio.Play();
		}
	}

	// ── Clipboard transitions ─────────────────────────────────────────────────

	/// <summary>
	/// Instantly hides a clipboard without animation (used during initialization).
	/// Uses a near-zero scale instead of zero to avoid Jolt Physics singular-transform warnings.
	/// </summary>
	private void HideClipboard(Node3D clipboard)
	{
		if (clipboard == null) return;
		clipboard.Visible = false;
		clipboard.Scale   = new Vector3(0.001f, 0.001f, 0.001f);
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

	// ── VFX helpers ───────────────────────────────────────────────────────────

	private void AddBurnFlash(Node3D target)
	{
		if (target == null || target.GetParent() == null) return;

		OmniLight3D flash = new OmniLight3D();
		flash.LightColor  = TransitionColor;
		flash.LightEnergy = 0.0f;
		flash.OmniRange   = 6.0f;

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

		// Capture position before the scale tween might shift the object.
		Vector3 spawnPos = target.GlobalPosition;

		CpuParticles3D particles = new CpuParticles3D();
		particles.TopLevel = true; // Prevent parent scale from squashing the burst.
		target.GetParent().AddChild(particles);
		particles.GlobalPosition     = spawnPos;
		particles.Amount             = 120;
		particles.Lifetime           = TransitionDuration * 1.5f;
		particles.OneShot            = true;
		particles.Explosiveness      = 0.85f;
		particles.EmissionShape      = CpuParticles3D.EmissionShapeEnum.Sphere;
		particles.EmissionSphereRadius = 0.15f;
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

		QuadMesh qm = new QuadMesh();
		qm.Size = new Vector2(0.018f, 0.018f);
		particles.Mesh = qm;

		StandardMaterial3D mat = new StandardMaterial3D();
		mat.ShadingMode            = StandardMaterial3D.ShadingModeEnum.Unshaded;
		mat.VertexColorUseAsAlbedo = true;
		mat.BillboardMode          = StandardMaterial3D.BillboardModeEnum.Enabled;
		mat.Transparency           = StandardMaterial3D.TransparencyEnum.Alpha;
		particles.MaterialOverride = mat;

		particles.Emitting = true;
		GetTree().CreateTimer(particles.Lifetime + 0.5f).Timeout += () => particles.QueueFree();
	}

	private void AddSmoke(Node3D target)
	{
		if (target == null || target.GetParent() == null) return;

		Vector3 spawnPos = target.GlobalPosition;

		CpuParticles3D smoke = new CpuParticles3D();
		smoke.TopLevel = true; // Prevent parent scale from squashing the burst.
		target.GetParent().AddChild(smoke);
		smoke.GlobalPosition     = spawnPos;
		smoke.Amount             = 30;
		smoke.Lifetime           = TransitionDuration * 2.5f;
		smoke.OneShot            = true;
		smoke.Explosiveness      = 0.5f;
		smoke.EmissionShape      = CpuParticles3D.EmissionShapeEnum.Sphere;
		smoke.EmissionSphereRadius = 0.12f;
		smoke.Direction          = new Vector3(0, 1, 0);
		smoke.Spread             = 35.0f;
		smoke.Gravity            = new Vector3(0, 0.5f, 0);
		smoke.InitialVelocityMin = 0.5f;
		smoke.InitialVelocityMax = 0.8f;
		smoke.ScaleAmountMin     = 1.0f;
		smoke.ScaleAmountMax     = 3.0f;

		Gradient smokeGradient = new Gradient();
		smokeGradient.SetColor(0, new Color(0.15f, 0.1f, 0.08f, 0.4f));
		smokeGradient.SetColor(1, new Color(0.1f, 0.08f, 0.06f, 0.0f));
		smoke.ColorRamp = smokeGradient;

		QuadMesh smokeQuad = new QuadMesh();
		smokeQuad.Size = new Vector2(0.06f, 0.06f);
		smoke.Mesh     = smokeQuad;

		StandardMaterial3D smokeMat = new StandardMaterial3D();
		smokeMat.ShadingMode            = StandardMaterial3D.ShadingModeEnum.Unshaded;
		smokeMat.VertexColorUseAsAlbedo = true;
		smokeMat.BillboardMode          = StandardMaterial3D.BillboardModeEnum.Enabled;
		smokeMat.Transparency           = StandardMaterial3D.TransparencyEnum.Alpha;
		smoke.MaterialOverride = smokeMat;

		smoke.Emitting = true;
		GetTree().CreateTimer(smoke.Lifetime + 1.0f).Timeout += () => smoke.QueueFree();
	}

	// ── Ambient fire helpers ──────────────────────────────────────────────────

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
