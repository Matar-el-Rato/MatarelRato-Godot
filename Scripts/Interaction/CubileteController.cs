using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

public partial class CubileteController : Node3D
{
	[Export] public NodePath CubileteMeshPath;
	[Export] public NodePath[] DicePaths;
	[Export] public NodePath InteractablePath;
	
	[ExportGroup("Physics")]
	[Export] public float ThrowForce = 2f;
	[Export] public float RandomRotationForce = 0.5f;
	
	[ExportGroup("Positions")]
	[Export] public Vector3 HoldOffset = new Vector3(0, -0.4f, -0.4f); // To where it tweens when grabbed
	[Export] public Vector3 ThrowOriginOffset = new Vector3(0, -0.3f, -0.5f); // Where dice are spawned relative to camera
	
	private enum State { Stationary, Held, Rolling, Resetting }
	private State _currentState = State.Stationary;
	
	private Node3D _cubileteMesh;
	private RigidBody3D[] _dice;
	private Interactable _interactable;
	private Node3D _playerCamera;
	
	private Transform3D _originalTransform;

	public override void _Ready()
	{
		_cubileteMesh = GetNode<Node3D>(CubileteMeshPath);
		_interactable = GetNode<Interactable>(InteractablePath);
		
		_dice = new RigidBody3D[DicePaths.Length];
		for (int i = 0; i < DicePaths.Length; i++)
		{
			_dice[i] = GetNode<RigidBody3D>(DicePaths[i]);
			_dice[i].Freeze = true;
			_dice[i].Visible = true;
			
			// Connect collision signal
			int diceIndex = i;
			_dice[i].BodyEntered += (body) => OnDiceCollision(diceIndex, body);
		}
		
		_interactable.Interacted += OnInteracted;
		
		_originalTransform = GlobalTransform;
		
		CallDeferred(MethodName.FindPlayerCamera);
	}

	private void OnDiceCollision(int index, Node body)
	{
		var die = _dice[index];
		var soundPlayer = die.GetNodeOrNull<AudioStreamPlayer3D>("CollisionSound");
		
		if (soundPlayer == null) return;

		// Volume attenuation based on impact velocity
		float velocity = die.LinearVelocity.Length();
		
		// Threshold to avoid sound spam from micro-movements
		if (velocity < 0.15f) return;

		// Map velocity to volume 
		// Max volume reached at 4.0 velocity, with an overall -6dB shift to lower base volume
		float intensity = Mathf.Clamp(velocity / 4.0f, 0.0f, 1.0f);
		float volume = Mathf.LinearToDb(intensity) - 6.0f;
		
		// Random pitch for variety (slightly wider range)
		soundPlayer.PitchScale = (float)GD.RandRange(0.85, 1.15);
		soundPlayer.VolumeDb = volume;
		soundPlayer.Play();
	}

	private void FindPlayerCamera()
	{
		var player = GetTree().Root.FindChild("Player", true, false);
		if (player != null)
		{
			_playerCamera = player.FindChild("Camera3D", true, false) as Node3D;
		}
	}

	public override void _Process(double delta)
	{
		// We no longer follow the camera in _Process while held, 
		// because the cup is hidden after the grab animation.
	}

	public override void _Input(InputEvent @event)
	{
		if (_currentState == State.Held)
		{
			bool isInteractPressed = @event.IsActionPressed("interact");
			bool isLeftClickPressed = @event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed;

			if (isInteractPressed || isLeftClickPressed)
			{
				GetViewport().SetInputAsHandled();
				StartRoll();
			}
		}
	}

	private void OnInteracted()
	{
		if (_currentState == State.Stationary)
		{
			Grab();
		}
	}

	private async void Grab()
	{
		_currentState = State.Resetting; // Use a transition state to avoid multi-clicks
		_interactable.PromptText = "Picking up...";
		_interactable.ProcessMode = ProcessModeEnum.Disabled;

		// 1. Animate towards "below camera"
		if (_playerCamera != null)
		{
			var targetTransform = _playerCamera.GlobalTransform.TranslatedLocal(HoldOffset);
			
			var tween = CreateTween();
			tween.SetParallel(true);
			tween.SetTrans(Tween.TransitionType.Back);
			tween.SetEase(Tween.EaseType.In);
			
			tween.TweenProperty(this, "global_transform", targetTransform, 0.5f);
			
			foreach (var die in _dice)
			{
				die.Freeze = true;
				tween.TweenProperty(die, "global_position", targetTransform.Origin, 0.5f);
			}

			await ToSignal(tween, "finished");
		}

		_currentState = State.Held;
		_interactable.PromptText = "Roll Dice";
		_interactable.ProcessMode = ProcessModeEnum.Inherit;
		Interactor.IsLocked = true;
		
		_cubileteMesh.Visible = false;
		foreach (var die in _dice)
		{
			die.Visible = false;
		}
	}

	private async void StartRoll()
	{
		_currentState = State.Rolling;
		_interactable.PromptText = "Waiting...";
		_interactable.ProcessMode = ProcessModeEnum.Disabled; 
		
		// 1. Release dice from a point in front of the camera (pitch-dependent but clamped)
		Vector3 camForward = -_playerCamera.GlobalTransform.Basis.Z;
		Vector3 camRight = _playerCamera.GlobalTransform.Basis.X;
		
		// Clamp the baseline direction for spawning and throwing
		// Limit downward pitch to -10 degrees (horizon is 0) to avoid floor issues
		float minEle = Mathf.DegToRad(-10f);
		Vector3 clampedForward = camForward;
		if (Mathf.Asin(clampedForward.Y) < minEle)
		{
			Vector3 horizontal = new Vector3(clampedForward.X, 0, clampedForward.Z).Normalized();
			if (horizontal.LengthSquared() < 0.001f) horizontal = Vector3.Forward; // fallback
			clampedForward = horizontal * Mathf.Cos(minEle) + Vector3.Up * Mathf.Sin(minEle);
		}
		
		// Spawn dice 0.4m along the clamped forward, slightly below center
		Vector3 throwPos = _playerCamera.GlobalPosition + (clampedForward * 0.4f) + (Vector3.Down * 0.1f);
		GlobalPosition = throwPos;

		for (int i = 0; i < _dice.Length; i++)
		{
			var die = _dice[i];
			die.Freeze = true;
			die.LinearVelocity = Vector3.Zero;
			die.AngularVelocity = Vector3.Zero;
			
			// Slightly more random spread
			die.GlobalPosition = throwPos + (camRight * (i == 0 ? -0.05f : 0.05f)) + (Vector3.Up * (float)GD.RandRange(-0.02, 0.02));
			die.Visible = true;
			die.Freeze = false;
			
			// ENFORCE MINIMUM ELEVATION: Use the clamped camera forward + slight upward bias for better feel
			Vector3 throwDir = (clampedForward + Vector3.Up * 0.1f).Normalized();
			
			float forceScale = ThrowForce;
			Vector3 impulse = (throwDir * ThrowForce) + new Vector3(
				(float)GD.RandRange(-0.1f, 0.1f) * forceScale,
				(float)GD.RandRange(0.05f, 0.15f) * forceScale, // Slight extra air
				(float)GD.RandRange(-0.1f, 0.1f) * forceScale
			);
			die.ApplyCentralImpulse(impulse);
			
			float torqueScale = Mathf.Clamp(RandomRotationForce, 0.1f, 2.0f);
			Vector3 torque = new Vector3(
				(float)GD.RandRange(-1, 1),
				(float)GD.RandRange(-1, 1),
				(float)GD.RandRange(-1, 1)
			) * torqueScale;
			die.ApplyTorqueImpulse(torque);
		}
		
		// Wait for dice to settle using Godot's sleeping system or timeout
		bool allAtRest = false;
		int timeoutTicks = 0;
		while (!allAtRest && timeoutTicks < 150) // ~15 seconds max
		{
			await Task.Delay(100);
			timeoutTicks++;
			
			allAtRest = true;
			foreach (var die in _dice)
			{
				// If not sleeping and still moving significantly, it's not at rest
				if (!die.Sleeping && die.LinearVelocity.Length() > 0.01f)
				{
					allAtRest = false;
					break;
				}
			}
		}
		
		CalculateResults();
		await Task.Delay(1000);
		ResetPosition();
	}

	private void CalculateResults()
	{
		List<int> values = new List<int>();
		foreach (var die in _dice)
		{
			values.Add(GetDiceValue(die));
		}
		
		string resultText = $"[color=#ffaa00][DICE][/color] You rolled: {string.Join(", ", values)} (Total: {GetTotal(values)})";
		ChatManager.AddLog(resultText);
	}

	private int GetDiceValue(RigidBody3D die)
	{
		Vector3 worldUp = Vector3.Up;
		Basis b = die.GlobalTransform.Basis;
		
		float maxDot = -2.0f;
		int value = 0;
		
		float dot = b.X.Dot(worldUp);
		if (dot > maxDot) { maxDot = dot; value = 4; }
		
		dot = (-b.X).Dot(worldUp);
		if (dot > maxDot) { maxDot = dot; value = 3; }
		
		dot = b.Y.Dot(worldUp);
		if (dot > maxDot) { maxDot = dot; value = 2; }
		
		dot = (-b.Y).Dot(worldUp);
		if (dot > maxDot) { maxDot = dot; value = 5; }
		
		dot = b.Z.Dot(worldUp);
		if (dot > maxDot) { maxDot = dot; value = 6; }
		
		dot = (-b.Z).Dot(worldUp);
		if (dot > maxDot) { maxDot = dot; value = 1; }
		
		return value;
	}

	private int GetTotal(List<int> values)
	{
		int sum = 0;
		foreach (int v in values) sum += v;
		return sum;
	}

	private void ResetPosition()
	{
		_currentState = State.Resetting;
		
		_cubileteMesh.Visible = true;
		
		var tween = CreateTween();
		tween.SetParallel(true);
		tween.SetTrans(Tween.TransitionType.Cubic);
		tween.SetEase(Tween.EaseType.Out);
		
		tween.TweenProperty(this, "global_transform", _originalTransform, 1.2f);
		
		for (int i = 0; i < _dice.Length; i++)
		{
			var die = _dice[i];
			die.Freeze = true;
			die.Visible = true;
			tween.TweenProperty(die, "global_position", _originalTransform.Origin + new Vector3(0, 0.02f + (0.02f * i), 0), 1.2f);
			tween.TweenProperty(die, "quaternion", new Quaternion(_originalTransform.Basis), 1.2f);
		}
		
		tween.Finished += () => {
			_currentState = State.Stationary;
			_interactable.PromptText = "Grab Cubilete";
			_interactable.ProcessMode = ProcessModeEnum.Inherit;
			Interactor.IsLocked = false;
			
			foreach (var die in _dice)
			{
				die.LinearVelocity = Vector3.Zero;
				die.AngularVelocity = Vector3.Zero;
			}
		};
	}
}
