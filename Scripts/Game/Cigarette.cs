// ═══════════════════════════════════════════════════
// Cigarette.cs
// Interactable cigarette prop that plays a 3-second
// automated smoke sequence: grab → smoke → return.
// Locks the Interactor while the sequence runs.
// ═══════════════════════════════════════════════════
using Godot;
using System;
using System.Threading.Tasks;

/// <summary>
/// Manages the cigarette prop interaction sequence:
/// 1. Player interacts → model reparented to camera and tweened to <see cref="HandPosition"/>.
/// 2. Smoke particles activate; audio plays.
/// 3. After the smoke duration, model tweens back to its original local transform.
/// 4. Puff particles fire at camera position; smoke idle resumes.
/// <see cref="Interactor.IsLocked"/> is held true for the duration.
/// </summary>
public partial class Cigarette : Node3D
{
	[ExportGroup("Hand Positioning")]
	/// <summary>Local position relative to the camera while held (centred, slightly below eye).</summary>
	[Export] public Vector3 HandPosition       = new Vector3(0f, -0.07f, -0.1f);
	/// <summary>Local rotation (radians) when held.</summary>
	[Export] public Vector3 HandRotation       = new Vector3(Mathf.Pi/2, 0, 0);
	[Export] public float   TransitionTime     = 0.5f;
	/// <summary>Total duration of the full grab → smoke → return cycle in seconds.</summary>
	[Export] public float   TotalSequenceTime  = 3.0f;

	[ExportGroup("Components")]
	[Export] public NodePath InteractablePath;
	[Export] public NodePath CigaretteModelPath;
	[Export] public NodePath AudioPlayerPath;
	/// <summary>Continuous idle smoke particles (emitting = false while stowed).</summary>
	[Export] public NodePath SmokeParticlesPath;
	/// <summary>One-shot puff burst triggered at the end of the sequence.</summary>
	[Export] public NodePath PuffParticlesPath;

	private Node3D              _cigaretteModel;
	private Interactable        _interactable;
	private AudioStreamPlayer3D _audioPlayer;
	private GpuParticles3D      _smokeParticles;
	private GpuParticles3D      _puffParticles;

	private Vector3 _originalLocalPos;
	private Vector3 _originalLocalRot;
	private Node    _originalParent;
	private bool    _isBusy = false;

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

	// ── Sequence ──────────────────────────────────────────────────────────────

	/// <summary>
	/// Runs the full 3-step smoking sequence asynchronously.
	/// Guards against re-entry with <see cref="_isBusy"/>.
	/// </summary>
	private async void StartSmokingSequence()
	{
		if (_isBusy || _cigaretteModel == null) return;
		_isBusy = true;

		_originalParent   = _cigaretteModel.GetParent();
		_originalLocalPos = _cigaretteModel.Position;
		_originalLocalRot = _cigaretteModel.Rotation;

		SetCollisionsEnabled(this, false);

		// Stop idle smoke while the cigarette is in hand.
		if (_smokeParticles != null)
			_smokeParticles.Emitting = false;

		_audioPlayer?.Play();

		var camera = GetViewport().GetCamera3D();
		if (camera != null)
		{
			// Step 1: grab (tween model to camera hand position).
			_cigaretteModel.Reparent(camera, true);
			var tweenIn = CreateTween();
			tweenIn.SetParallel(true);
			tweenIn.TweenProperty(_cigaretteModel, "position", HandPosition, TransitionTime).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
			tweenIn.TweenProperty(_cigaretteModel, "rotation", HandRotation, TransitionTime).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);

			await ToSignal(tweenIn, Tween.SignalName.Finished);
			Interactor.IsLocked = true;

			// Step 2: smoke (idle wait, duration = total - 2 * transition).
			float smokeTime = Mathf.Max(0.1f, TotalSequenceTime - (TransitionTime * 2.0f));
			await Task.Delay((int)(smokeTime * 1000));

			// Step 3: return to world.
			ReturnToPlace();
		}
		else
		{
			// No camera found — abort gracefully.
			_isBusy = false;
			SetCollisionsEnabled(this, true);
		}
	}

	/// <summary>
	/// Tweens the cigarette model back to its original local transform,
	/// fires puff particles at the camera, and re-enables smoke idle.
	/// </summary>
	private void ReturnToPlace()
	{
		if (_puffParticles != null)
		{
			var camera = GetViewport().GetCamera3D();
			if (camera != null)
			{
				_puffParticles.GlobalPosition = camera.GlobalPosition;
				_puffParticles.GlobalRotation = camera.GlobalRotation;
				// Offset slightly forward and downward for a natural exhale position.
				_puffParticles.Translate(new Vector3(0, -0.1f, -0.5f));
			}
			_puffParticles.Emitting = true;
		}

		_cigaretteModel.Reparent(_originalParent, true);

		var tweenOut = CreateTween();
		tweenOut.SetParallel(true);
		tweenOut.TweenProperty(_cigaretteModel, "position", _originalLocalPos, TransitionTime).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		tweenOut.TweenProperty(_cigaretteModel, "rotation", _originalLocalRot, TransitionTime).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);

		tweenOut.Finished += () =>
		{
			_isBusy = false;
			SetCollisionsEnabled(this, true);
			if (_smokeParticles != null)
				_smokeParticles.Emitting = true;
			Interactor.IsLocked = false;
		};
	}

	// ── Collision helper ──────────────────────────────────────────────────────

	/// <summary>
	/// Recursively enables or disables all physics collision objects in the subtree.
	/// Prevents the cigarette from triggering physics events while held.
	/// </summary>
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
