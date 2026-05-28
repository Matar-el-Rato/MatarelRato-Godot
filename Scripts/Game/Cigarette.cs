// ═══════════════════════════════════════════════════
// Cigarette.cs
// One-use interactable: available only after dice roll and before the player
// moves a piece. Smoking sequence → SmokingComplete event (TableManager sends
// server action). Server replies with cigarette_result; TableManager then calls
// BurnDisappear(). Remote clients just call BurnDisappear() directly.
// ═══════════════════════════════════════════════════
using Godot;
using System.Threading.Tasks;

/// <summary>
/// Manages the cigarette smoking sequence:
/// 1. Player interacts → model reparented to camera, tweened to HandPosition.
/// 2. Smoke particles start; audio plays.
/// 3. After smoke duration → puff particles fire, <see cref="SmokingComplete"/> fires.
///    The model stays in hand; TableManager waits for cigarette_result and then calls
///    <see cref="BurnDisappear"/>, which arcs the cigarette back to the ashtray and burns
///    it there (the smoking audio plays through until that point).
/// </summary>
public partial class Cigarette : Node3D
{
	[ExportGroup("Hand Positioning")]
	[Export] public Vector3 HandPosition      = new Vector3(0f, -0.07f, -0.1f);
	[Export] public Vector3 HandRotation      = new Vector3(Mathf.Pi / 2f, 0f, 0f);
	[Export] public float   TransitionTime    = 0.5f;
	/// <summary>Total duration of the grab → smoke portion of the cycle (not including return).</summary>
	[Export] public float   TotalSequenceTime = 3.0f;

	[ExportGroup("Components")]
	[Export] public NodePath InteractablePath;
	[Export] public NodePath CigaretteModelPath;
	[Export] public NodePath AudioPlayerPath;
	/// <summary>Continuous idle smoke particles (emitting = false while stowed).</summary>
	[Export] public NodePath SmokeParticlesPath;
	/// <summary>One-shot puff burst triggered at the end of the sequence.</summary>
	[Export] public NodePath PuffParticlesPath;

	// ── Signals ───────────────────────────────────────────────────────────────

	/// <summary>
	/// Fired when the smoke animation finishes.
	/// TableManager should send the server "use_cigarette" action, then wait for
	/// cigarette_result before calling BurnDisappear().
	/// </summary>
	[Signal] public delegate void SmokingCompleteEventHandler();

	// ── State ─────────────────────────────────────────────────────────────────

	private Node3D              _cigaretteModel;
	private Interactable        _interactable;
	private AudioStreamPlayer3D _audioPlayer;
	private GpuParticles3D      _smokeParticles;
	private GpuParticles3D      _puffParticles;

	private Vector3 _originalLocalPos;
	private Vector3 _originalLocalRot;
	private Vector3 _originalLocalScale = Vector3.One;
	private Node    _originalParent;
	private bool    _isBusy = false;
	private bool    _isDisappearing = false;
	/// <summary>True once StartSmokingSequence reparents _cigaretteModel to the camera.</summary>
	private bool    _meshReparentedToCamera = false;
	/// <summary>Glowing tip light, parented to the model so it follows and frees with it.</summary>
	private OmniLight3D _ember;

	private static readonly Color _burnColor = new Color(1.0f, 0.5f, 0.2f);

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_cigaretteModel = GetNodeOrNull<Node3D>(CigaretteModelPath);
		_interactable   = GetNodeOrNull<Interactable>(InteractablePath);
		_audioPlayer    = GetNodeOrNull<AudioStreamPlayer3D>(AudioPlayerPath);
		_smokeParticles = GetNodeOrNull<GpuParticles3D>(SmokeParticlesPath);
		_puffParticles  = GetNodeOrNull<GpuParticles3D>(PuffParticlesPath);

		if (_interactable != null)
			_interactable.Interacted += StartSmokingSequence;
	}

	// ── Public API ────────────────────────────────────────────────────────────

	/// <summary>
	/// Enables or disables interaction. TableManager gates this so the cigarette
	/// is only usable between dice_result and the player's first move.
	/// </summary>
	public void SetInteractionEnabled(bool enabled)
	{
		if (_interactable != null)
			_interactable.Enabled = enabled;
	}

	/// <summary>
	/// Plays fire VFX on the model (wherever it currently is) then removes this node.
	/// Call after cigarette_result is received from the server, or immediately for remote clients.
	/// </summary>
	public void BurnDisappear()
	{
		if (_isDisappearing) return;
		_isDisappearing = true;

		SetInteractionEnabled(false);
		SetCollisionsEnabled(this, false);

		// Audio keeps playing through the return — it's stopped in BurnAtAshtray so the
		// smoking sound isn't cut off mid-clip.
		// Hold a beat, then arc the cigarette back to the ashtray before it burns away.
		GetTree().CreateTimer(0.5f).Timeout += ReturnThenBurn;
	}

	/// <summary>
	/// Brings the cigarette back from the player's hand to its ashtray rest spot, then
	/// burns it there — instead of consuming it in front of the camera.
	/// </summary>
	private void ReturnThenBurn()
	{
		if (!IsInsideTree()) return;

		if (_meshReparentedToCamera && _cigaretteModel != null && IsInstanceValid(_cigaretteModel)
			&& _originalParent != null && IsInstanceValid(_originalParent))
		{
			// Reparent back to the table (keep global transform), then tween to the rest pose.
			_meshReparentedToCamera = false;
			_cigaretteModel.Reparent(_originalParent, true);
			_cigaretteModel.Visible = true;

			var returnTween = _cigaretteModel.CreateTween().SetParallel(true);
			returnTween.TweenProperty(_cigaretteModel, "position", _originalLocalPos, TransitionTime)
					   .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
			returnTween.TweenProperty(_cigaretteModel, "rotation", _originalLocalRot, TransitionTime)
					   .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
			returnTween.Finished += BurnAtAshtray;
		}
		else
		{
			// Remote clients (never grabbed) or no model — burn in place on the table.
			BurnAtAshtray();
		}
	}

	private void BurnAtAshtray()
	{
		if (!IsInsideTree()) return;

		// Now that the cigarette is resting in the ashtray, stop the smoking sound and burn it.
		_audioPlayer?.Stop();
		if (_smokeParticles != null) _smokeParticles.Emitting = false;

		Node3D model   = _cigaretteModel != null && IsInstanceValid(_cigaretteModel) ? _cigaretteModel : null;
		Vector3 burnPos = model != null ? model.GlobalPosition : GlobalPosition;

		if (model != null) model.Visible = true;
		AddBurnFlash(burnPos);
		AddEmbers(burnPos);

		Node3D scaleTarget = model ?? this;
		var tween = scaleTarget.CreateTween();
		tween.TweenProperty(scaleTarget, "scale", new Vector3(0.001f, 0.001f, 0.001f), 0.5f)
			 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
		tween.Finished += ResetToFresh;
	}

	/// <summary>
	/// Resets the cigarette back to its hidden "fresh" state so a future golden-square grant
	/// can re-spawn it. Does NOT QueueFree — PlayerItemSet looks the item up by name
	/// ("CigaretteInteraction") in SpawnItem; freeing the node would make any subsequent
	/// grant fail with "'CigaretteInteraction' not found".
	/// </summary>
	private void ResetToFresh()
	{
		if (!IsInsideTree()) return;

		// Restore the model's local transform under the root so SpawnItem's scale-in works.
		if (_cigaretteModel != null && IsInstanceValid(_cigaretteModel))
		{
			_cigaretteModel.Position = _originalLocalPos;
			_cigaretteModel.Rotation = _originalLocalRot;
			_cigaretteModel.Scale    = _originalLocalScale;
			_cigaretteModel.Visible  = true;
		}

		// Hide the whole prop and disable interaction — matches PlayerItemSet.HideAndDisableAll.
		Visible = false;
		SetCollisionsEnabled(this, false);
		if (_interactable != null) _interactable.Enabled = false;

		// Kill the ember's looping pulse and free it — otherwise it'd keep glowing forever
		// as a hidden child of the model and reappear when the next grant scales it back in.
		if (_ember != null && IsInstanceValid(_ember)) _ember.QueueFree();
		_ember = null;

		// Clear consumption flags so a subsequent grant can run StartSmokingSequence cleanly.
		_isDisappearing         = false;
		_isBusy                 = false;
		_meshReparentedToCamera = false;
	}

	// ── Sequence ──────────────────────────────────────────────────────────────

	private async void StartSmokingSequence()
	{
		if (_isBusy || _cigaretteModel == null) return;
		_isBusy = true;

		// Disable interaction immediately so it can't be triggered again.
		SetInteractionEnabled(false);

		_originalParent     = _cigaretteModel.GetParent();
		_originalLocalPos   = _cigaretteModel.Position;
		_originalLocalRot   = _cigaretteModel.Rotation;
		_originalLocalScale = _cigaretteModel.Scale;

		SetCollisionsEnabled(this, false);

		if (_smokeParticles != null)
			_smokeParticles.Emitting = false;

		// Smoking is a first-person action, but the audio node lives at the table — disable
		// distance attenuation and push the volume so the sound is present, not faint.
		if (_audioPlayer != null)
		{
			_audioPlayer.AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled;
			_audioPlayer.VolumeDb         = 4f;
			_audioPlayer.Play();
		}

		var camera = GetViewport().GetCamera3D();
		if (camera == null)
		{
			_isBusy = false;
			SetCollisionsEnabled(this, true);
			SetInteractionEnabled(true);
			return;
		}

		// Step 1: Grab — tween model to camera hand position.
		_cigaretteModel.Reparent(camera, true);
		_meshReparentedToCamera = true;
		var tweenIn = CreateTween().SetParallel(true);
		tweenIn.TweenProperty(_cigaretteModel, "position", HandPosition, TransitionTime)
			   .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
		tweenIn.TweenProperty(_cigaretteModel, "rotation", HandRotation, TransitionTime)
			   .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
		await ToSignal(tweenIn, Tween.SignalName.Finished);
		if (!IsInsideTree()) return;

		Interactor.IsLocked = true;

		// A glowing ember at the tip gives the smoking sequence real presence.
		SpawnEmber();

		// Step 2: Smoke — idle wait.
		float smokeTime = Mathf.Max(0.1f, TotalSequenceTime - TransitionTime * 2f);
		await Task.Delay((int)(smokeTime * 1000));
		if (!IsInsideTree()) return;

		// Step 3: Puff and signal. The smoking audio keeps playing — it's stopped only
		// once the cigarette is stubbed out in the ashtray, so it isn't cut off abruptly.
		FirePuffParticles();
		if (_smokeParticles != null) _smokeParticles.Emitting = false;

		Interactor.IsLocked = false;
		_isBusy = false;

		// Notify TableManager; it will call BurnDisappear() after cigarette_result arrives.
		EmitSignal(SignalName.SmokingComplete);
	}

	private void FirePuffParticles()
	{
		if (_puffParticles == null) return;
		// Beefier exhale cloud for a more satisfying finish.
		_puffParticles.Amount = 64;
		var camera = GetViewport().GetCamera3D();
		if (camera != null)
		{
			_puffParticles.GlobalPosition = camera.GlobalPosition;
			_puffParticles.GlobalRotation = camera.GlobalRotation;
			_puffParticles.Translate(new Vector3(0f, -0.1f, -0.5f));
		}
		_puffParticles.Emitting = true;

		// Brief flare on the exhale, then settle back into the pulse range.
		if (_ember != null && IsInstanceValid(_ember))
		{
			var flare = _ember.CreateTween();
			flare.TweenProperty(_ember, "light_energy", 1.6f, 0.12f);
			flare.TweenProperty(_ember, "light_energy", 0.6f, 0.4f);
		}
	}

	/// <summary>
	/// Spawns a small pulsing orange light at the cigarette tip. Parented to the model so
	/// it follows the cigarette into the hand and is freed automatically when the model is.
	/// </summary>
	private void SpawnEmber()
	{
		if (_cigaretteModel == null || !IsInstanceValid(_cigaretteModel)) return;

		_ember = new OmniLight3D
		{
			LightColor      = new Color(1f, 0.4f, 0.12f),
			LightEnergy     = 0.5f,
			OmniRange       = 0.3f,
			OmniAttenuation = 1.8f,
			ShadowEnabled   = false,
		};
		_cigaretteModel.AddChild(_ember);
		_ember.Position = Vector3.Zero;

		// Subtle draw/cool pulse, as if being smoked.
		var pulse = _ember.CreateTween().SetLoops();
		pulse.TweenProperty(_ember, "light_energy", 1.1f, 0.5f)
		     .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		pulse.TweenProperty(_ember, "light_energy", 0.35f, 0.7f)
		     .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
	}

	// ── VFX ───────────────────────────────────────────────────────────────────

	private void AddBurnFlash(Vector3 worldPos)
	{
		var flash = new OmniLight3D
		{
			TopLevel    = true,
			LightColor  = _burnColor,
			LightEnergy = 0f,
			OmniRange   = 4f,
		};
		AddChild(flash);
		flash.GlobalPosition = worldPos + Vector3.Up * 0.2f;

		var t = CreateTween();
		t.TweenProperty(flash, "light_energy", 4f, 0.07f);
		t.TweenProperty(flash, "light_energy", 0f, 0.38f);
		t.Finished += () => flash.QueueFree();
	}

	private void AddEmbers(Vector3 worldPos)
	{
		var particles = new CpuParticles3D { TopLevel = true };
		AddChild(particles);
		particles.GlobalPosition     = worldPos + Vector3.Up * 0.2f;
		particles.Amount             = 80;
		particles.Lifetime           = 0.8f;
		particles.OneShot            = true;
		particles.Explosiveness      = 0.85f;
		particles.EmissionShape      = CpuParticles3D.EmissionShapeEnum.Box;
		particles.EmissionBoxExtents = new Vector3(0.15f, 0.25f, 0.15f);
		particles.Direction          = new Vector3(0f, 1f, 0f);
		particles.Spread             = 50f;
		particles.Gravity            = new Vector3(0f, 2f, 0f);
		particles.InitialVelocityMin = 0.6f;
		particles.InitialVelocityMax = 1.8f;
		particles.ScaleAmountMin     = 0.6f;
		particles.ScaleAmountMax     = 1.2f;

		var gradient = new Gradient();
		gradient.SetColor(0, new Color(1f, 1f, 0.5f, 1f));
		gradient.AddPoint(0.3f, new Color(1f, 0.5f, 0.1f, 0.9f));
		gradient.SetColor(gradient.GetPointCount() - 1, new Color(0.8f, 0.1f, 0f, 0f));
		particles.ColorRamp = gradient;

		particles.Mesh = new QuadMesh { Size = new Vector2(0.015f, 0.015f) };
		particles.MaterialOverride = new StandardMaterial3D
		{
			ShadingMode            = StandardMaterial3D.ShadingModeEnum.Unshaded,
			VertexColorUseAsAlbedo = true,
			BillboardMode          = StandardMaterial3D.BillboardModeEnum.Enabled,
			Transparency           = StandardMaterial3D.TransparencyEnum.Alpha,
		};
		particles.Emitting = true;

		GetTree().CreateTimer(particles.Lifetime + 0.5f).Timeout +=
			() => { if (IsInstanceValid(particles)) particles.QueueFree(); };
	}

	// ── Collision helper ──────────────────────────────────────────────────────

	private void SetCollisionsEnabled(Node node, bool enabled)
	{
		if (node is CollisionObject3D collisionObject)
		{
			collisionObject.InputRayPickable = enabled;
			if (node is PhysicsBody3D body)
				body.SetDeferred(Node.PropertyName.ProcessMode,
					(int)(enabled ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled));
		}
		if (node is CollisionShape3D shape)
			shape.SetDeferred(CollisionShape3D.PropertyName.Disabled, !enabled);

		foreach (Node child in node.GetChildren())
			SetCollisionsEnabled(child, enabled);
	}
}
