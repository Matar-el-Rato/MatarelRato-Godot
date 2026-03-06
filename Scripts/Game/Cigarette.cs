using Godot;
using System;
using System.Threading.Tasks;

public partial class Cigarette : Node3D
{
	[ExportGroup("Hand Positioning")]
	[Export] public Vector3 HandPosition = new Vector3(0f, -0.07f, -0.1f); // Centered
	[Export] public Vector3 HandRotation = new Vector3(Mathf.Pi/2, 0, 0);
	[Export] public float TransitionTime = 0.5f;
	[Export] public float TotalSequenceTime = 3.0f;

	[ExportGroup("Components")]
	[Export] public NodePath InteractablePath;
	[Export] public NodePath CigaretteModelPath;
	[Export] public NodePath AudioPlayerPath;
	[Export] public NodePath SmokeParticlesPath;
	[Export] public NodePath PuffParticlesPath;

	private Node3D _cigaretteModel;
	private Interactable _interactable;
	private AudioStreamPlayer3D _audioPlayer;
	private GpuParticles3D _smokeParticles;
	private GpuParticles3D _puffParticles;

	private Vector3 _originalLocalPos;
	private Vector3 _originalLocalRot;
	private Node _originalParent;
	private bool _isBusy = false;

	public override void _Ready()
	{
		_cigaretteModel = GetNodeOrNull<Node3D>(CigaretteModelPath);
		_interactable = GetNodeOrNull<Interactable>(InteractablePath);
		_audioPlayer = GetNodeOrNull<AudioStreamPlayer3D>(AudioPlayerPath);
		_smokeParticles = GetNodeOrNull<GpuParticles3D>(SmokeParticlesPath);
		_puffParticles = GetNodeOrNull<GpuParticles3D>(PuffParticlesPath);

		if (_interactable != null)
		{
			_interactable.Interacted += StartSmokingSequence;
		}
	}

	private async void StartSmokingSequence()
	{
		if (_isBusy || _cigaretteModel == null) return;

		GD.Print("Cigarette: Starting 3s automated sequence.");
		_isBusy = true;
		
		_originalParent = _cigaretteModel.GetParent();
		_originalLocalPos = _cigaretteModel.Position;
		_originalLocalRot = _cigaretteModel.Rotation;

		// Disable collisions on the model/scene
		SetCollisionsEnabled(this, false);

		if (_smokeParticles != null)
		{
			_smokeParticles.Emitting = false;
		}

		if (_audioPlayer != null)
		{
			_audioPlayer.Play();
		}

		// Find camera
		var camera = GetViewport().GetCamera3D();
		if (camera != null)
		{
			// 1. Grab (Transition ciggie to camera)
			_cigaretteModel.Reparent(camera, true);
			var tweenIn = CreateTween();
			tweenIn.SetParallel(true);
			tweenIn.TweenProperty(_cigaretteModel, "position", HandPosition, TransitionTime).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
			tweenIn.TweenProperty(_cigaretteModel, "rotation", HandRotation, TransitionTime).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
			
			await ToSignal(tweenIn, Tween.SignalName.Finished);
			GD.Print("Cigarette: Grabbed. Smoking...");

			// 2. Smoke (Wait)
			float smokeTime = Mathf.Max(0.1f, TotalSequenceTime - (TransitionTime * 2.0f));
			await Task.Delay((int)(smokeTime * 1000));
			GD.Print("Cigarette: Smoking finished. Returning...");

			// 3. Return
			ReturnToPlace();
		}
		else
		{
			_isBusy = false;
			SetCollisionsEnabled(this, true);
		}
	}

	private void SetCollisionsEnabled(Node node, bool enabled)
	{
		if (node is CollisionObject3D collisionObject)
		{
			collisionObject.InputRayPickable = enabled;
			if (node is PhysicsBody3D body)
			{
				body.SetDeferred(Node.PropertyName.ProcessMode, 
					(int)(enabled ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled));
			}
		}

		if (node is CollisionShape3D shape)
		{
			shape.SetDeferred(CollisionShape3D.PropertyName.Disabled, !enabled);
		}

		foreach (Node child in node.GetChildren())
		{
			SetCollisionsEnabled(child, enabled);
		}
	}

	private void ReturnToPlace()
	{
		if (_puffParticles != null)
		{
			var camera = GetViewport().GetCamera3D();
			if (camera != null)
			{
				_puffParticles.GlobalPosition = camera.GlobalPosition;
				_puffParticles.GlobalRotation = camera.GlobalRotation;
				// Offset slightly forward/down as requested
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
			{
				_smokeParticles.Emitting = true;
			}
			GD.Print("Cigarette: Sequence complete. Back in world.");
		};
	}
}
