using Godot;
using System;
using System.Threading.Tasks;

public partial class Gun : Node3D
{
	[ExportGroup("Hand Positioning")]
	[Export] public Vector3 HandPosition = new Vector3(0.15f, -0.1f, -0.3f);
	[Export] public Vector3 HandRotation = new Vector3(0, Mathf.Pi, 0);
	[Export] public float TransitionTime = 0.5f;
	[Export] public double ReturnDelay = 1.0; //

	[ExportGroup("Components")]
	[Export] public NodePath InteractablePath;
	[Export] public NodePath MuzzleFlashPath;
	[Export] public NodePath AudioPlayerPath;
	[Export] public NodePath MeshRootPath = "makarov";

	private Node3D _meshRoot;
	private AnimationPlayer _animPlayer;
	private Interactable _interactable;
	private Node3D _muzzleFlash;
	private AudioStreamPlayer3D _audioPlayer;

	private Vector3 _originalPosition;
	private Vector3 _originalRotation;
	private Node _originalParent;
	private bool _isInHand = false;
	private double _timeSinceLastShot = 0;
	private bool _hasBeenShot = false;
	private bool _isReturning = false;
	private bool _isTransitioning = false;

	public override void _Ready()
	{
		_meshRoot = GetNodeOrNull<Node3D>(MeshRootPath);
		if (_meshRoot != null)
		{
			_animPlayer = _meshRoot.GetNodeOrNull<AnimationPlayer>("AnimationPlayer") ?? 
						  _meshRoot.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
			
			if (_animPlayer != null)
			{
				GD.Print($"Gun: AnimationPlayer found. Available animations:");
				foreach (string anim in _animPlayer.GetAnimationList())
				{
					GD.Print($" - {anim}");
				}
			}
			else
			{
				GD.PrintErr($"Gun: AnimationPlayer NOT found under '{MeshRootPath}'!");
			}
		}
		else
		{
			GD.PrintErr($"Gun: MeshRoot '{MeshRootPath}' NOT found!");
		}

		_interactable = GetNodeOrNull<Interactable>(InteractablePath);
		_muzzleFlash = GetNodeOrNull<Node3D>(MuzzleFlashPath);
		_audioPlayer = GetNodeOrNull<AudioStreamPlayer3D>(AudioPlayerPath);

		if (_interactable != null)
		{
			_interactable.Interacted += OnInteracted;
		}
	}

	private void OnInteracted()
	{
		if (_isInHand || _isTransitioning || _isReturning) return;

		GD.Print("Gun: Interacted, moving to hand.");
		_isTransitioning = true;
		_hasBeenShot = false; // Reset on pickup
		
		_originalParent = GetParent();
		_originalPosition = Position;
		_originalRotation = Rotation;

		// Disable collisions to prevent glitching with player
		SetCollisionsEnabled(this, false);

		// Find camera
		var camera = GetViewport().GetCamera3D();
		if (camera != null)
		{
			ReparentToHand(camera);
		}
	}

	private void SetCollisionsEnabled(Node node, bool enabled)
	{
		if (node is CollisionObject3D collisionObject)
		{
			collisionObject.InputRayPickable = enabled;
			// Disable the entire object from physics processing if it's a body
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

	private void ReparentToHand(Node3D handParent)
	{
		// Smoothly move to hand
		var globalTrans = GlobalTransform;
		
		// Use Reparent in Godot 4
		Reparent(handParent, true);

		var tween = CreateTween();
		tween.SetParallel(true);
		tween.TweenProperty(this, "position", HandPosition, TransitionTime).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
		tween.TweenProperty(this, "rotation", HandRotation, TransitionTime).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
		
		tween.Finished += () => 
		{
			_isInHand = true;
			_isTransitioning = false;
			_timeSinceLastShot = 0;
			GD.Print("Gun: Now in hand.");
		};
	}

	public override void _Process(double delta)
	{
		if (_isInHand && _hasBeenShot && !_isReturning && !_isTransitioning)
		{
			_timeSinceLastShot += delta;
			if (_timeSinceLastShot >= ReturnDelay)
			{
				ReturnToPlace();
			}
		}
	}

	public override void _Input(InputEvent @event)
	{
		// Ensure we check for left click interaction via Interactor.cs mechanism
		// But for shooting, we use standard input if it's already in hand
		if (_isInHand && !_isReturning && !_isTransitioning)
		{
			if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
			{
				Shoot();
			}
		}
	}

	private void Shoot()
	{
		_timeSinceLastShot = 0;
		_hasBeenShot = true;
		GD.Print("Gun: Shooting!");

		if (_animPlayer != null)
		{
			// Use only the "Animation" 
			if (_animPlayer.HasAnimation("Animation")) 
			{
				_animPlayer.Stop();
				_animPlayer.SpeedScale = 2.0f; // Restoring 2x speed
				_animPlayer.Play("Animation");
			}
			else
			{
				GD.PrintErr("Gun: Animation 'Animation' not found in AnimationPlayer!");
			}
		}

		if (_audioPlayer != null)
		{
			_audioPlayer.Stop();
			_audioPlayer.Play();
		}

		ShowMuzzleFlash();
	}

	private void ShowMuzzleFlash()
	{
		if (_muzzleFlash == null) return;
		
		// Trigger BinbunVFX play() method
		if (_muzzleFlash.HasMethod("play"))
		{
			_muzzleFlash.Call("play");
		}
	}

	private void ReturnToPlace()
	{
		GD.Print("Gun: Returning to place.");
		_isReturning = true;
		_isInHand = false;

		Reparent(_originalParent, true);

		var tween = CreateTween();
		tween.SetParallel(true);
		tween.TweenProperty(this, "position", _originalPosition, TransitionTime).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		tween.TweenProperty(this, "rotation", _originalRotation, TransitionTime).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		
		tween.Finished += () => 
		{
			_isReturning = false;
			// Re-enable collisions when back in world
			SetCollisionsEnabled(this, true);
			GD.Print("Gun: Back in world.");
		};
	}
}
