// ═══════════════════════════════════════════════════
// CubileteController.cs
// Controls the cubilete (dice cup) prop: grab animation,
// physics dice throw, result calculation, and automatic reset.
// ═══════════════════════════════════════════════════
using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// Manages the full cubilete (dice cup) interaction sequence:
/// 1. Player interacts → cup tweens to hand position.
/// 2. Player presses interact/click again → dice are physics-launched.
/// 3. Script waits for dice to settle (or 15 s timeout).
/// 4. Face-up value of each die is determined by dot-product with world Up.
/// 5. Results posted to the chat log; cup resets to its original transform.
/// </summary>
public partial class CubileteController : Node3D
{
	[Export] public NodePath   CubileteMeshPath;
	[Export] public NodePath[] DicePaths;
	[Export] public NodePath   InteractablePath;

	[ExportGroup("Physics")]
	[Export] public float ThrowForce          = 2f;
	[Export] public float RandomRotationForce = 0.5f;

	[ExportGroup("Positions")]
	/// <summary>Local offset from the camera where the cup tweens when grabbed.</summary>
	[Export] public Vector3 HoldOffset        = new Vector3(0, -0.4f, -0.4f);
	/// <summary>World offset from the camera where dice are spawned at throw time.</summary>
	[Export] public Vector3 ThrowOriginOffset = new Vector3(0, -0.3f, -0.5f);

	private enum State { Stationary, Held, Rolling, Resetting }
	private State _currentState = State.Stationary;

	private Node3D       _cubileteMesh;
	private RigidBody3D[] _dice;
	private Interactable  _interactable;
	private Node3D        _playerCamera;

	// Saved on _Ready so the cup can return after a throw.
	// Stored as LOCAL transform so the reset target is correct even after the
	// parent (PlayingSetup) has been tweened to its final table height.
	private Transform3D _originalLocalTransform;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_cubileteMesh = GetNode<Node3D>(CubileteMeshPath);
		_interactable = GetNode<Interactable>(InteractablePath);

		_dice = new RigidBody3D[DicePaths.Length];
		for (int i = 0; i < DicePaths.Length; i++)
		{
			_dice[i]        = GetNode<RigidBody3D>(DicePaths[i]);
			_dice[i].Freeze = true;
			_dice[i].Visible = true;

			// Capture index for the closure — lambda captures by reference in C#.
			int diceIndex = i;
			_dice[i].BodyEntered += (body) => OnDiceCollision(diceIndex, body);
		}

		_interactable.Interacted    += OnInteracted;
		_originalLocalTransform      = Transform; // local to immediate parent

		CallDeferred(MethodName.FindPlayerCamera);
	}

	/// <summary>
	/// Locates the player's Camera3D node for use as the throw reference frame.
	/// </summary>
	private void FindPlayerCamera()
	{
		var player = GetTree().Root.FindChild("Player", true, false);
		if (player != null)
			_playerCamera = player.FindChild("Camera3D", true, false) as Node3D;
	}

	// ── Input ─────────────────────────────────────────────────────────────────

	public override void _Process(double delta)
	{
		// Roll timing is handled entirely inside StartRoll(); nothing to do per frame.
	}

	public override void _Input(InputEvent @event)
	{
		if (_currentState != State.Held) return;

		bool isInteractPressed   = @event.IsActionPressed("interact");
		bool isLeftClickPressed  = @event is InputEventMouseButton mb
								&& mb.ButtonIndex == MouseButton.Left
								&& mb.Pressed;

		if (isInteractPressed || isLeftClickPressed)
		{
			GetViewport().SetInputAsHandled();
			StartRoll();
		}
	}

	// ── State machine ─────────────────────────────────────────────────────────

	private void OnInteracted()
	{
		if (_currentState == State.Stationary)
			Grab();
	}

	/// <summary>
	/// Tweens the cup and dice toward the camera's "hold" position, then switches
	/// to <see cref="State.Held"/> and hides the meshes (they reappear on throw).
	/// </summary>
	private async void Grab()
	{
		// Use Resetting as a transition guard to prevent double-grabs.
		_currentState = State.Resetting;
		_interactable.PromptText    = "Picking up...";
		_interactable.ProcessMode   = ProcessModeEnum.Disabled;

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

		_currentState              = State.Held;
		_interactable.PromptText   = "Roll Dice";
		_interactable.ProcessMode  = ProcessModeEnum.Inherit;
		Interactor.IsLocked        = true;

		// Hide cup and dice until the throw — they reappear when launched.
		_cubileteMesh.Visible = false;
		foreach (var die in _dice)
			die.Visible = false;
	}

	/// <summary>
	/// Unfreezes dice, applies randomised throw impulses, then waits for them to settle
	/// before calculating and logging results.
	/// </summary>
	private async void StartRoll()
	{
		_currentState              = State.Rolling;
		_interactable.PromptText   = "Waiting...";
		_interactable.ProcessMode  = ProcessModeEnum.Disabled;

		Vector3 camForward = -_playerCamera.GlobalTransform.Basis.Z;
		Vector3 camRight   =  _playerCamera.GlobalTransform.Basis.X;

		// Clamp downward pitch so dice don't immediately hit the floor.
		float   minEle = Mathf.DegToRad(-10f);
		Vector3 clampedForward = camForward;
		if (Mathf.Asin(clampedForward.Y) < minEle)
		{
			Vector3 horizontal = new Vector3(clampedForward.X, 0, clampedForward.Z).Normalized();
			if (horizontal.LengthSquared() < 0.001f) horizontal = Vector3.Forward;
			clampedForward = horizontal * Mathf.Cos(minEle) + Vector3.Up * Mathf.Sin(minEle);
		}

		// Spawn point: 0.4 m forward, 0.1 m below camera centre.
		Vector3 throwPos = _playerCamera.GlobalPosition + (clampedForward * 0.4f) + (Vector3.Down * 0.1f);
		GlobalPosition = throwPos;

		for (int i = 0; i < _dice.Length; i++)
		{
			var die = _dice[i];
			die.Freeze          = true;
			die.LinearVelocity  = Vector3.Zero;
			die.AngularVelocity = Vector3.Zero;

			// Stagger dice slightly left/right so they don't overlap at spawn.
			die.GlobalPosition = throwPos
				+ (camRight * (i == 0 ? -0.05f : 0.05f))
				+ (Vector3.Up * (float)GD.RandRange(-0.02, 0.02));
			die.Visible = true;
			die.Freeze  = false;

			// Apply a slight upward bias so dice arc rather than scrape the floor.
			Vector3 throwDir    = (clampedForward + Vector3.Up * 0.1f).Normalized();
			float   forceScale  = ThrowForce;
			Vector3 impulse     = (throwDir * ThrowForce) + new Vector3(
				(float)GD.RandRange(-0.1f, 0.1f) * forceScale,
				(float)GD.RandRange(0.05f, 0.15f) * forceScale,
				(float)GD.RandRange(-0.1f, 0.1f) * forceScale
			);
			die.ApplyCentralImpulse(impulse);

			float   torqueScale = Mathf.Clamp(RandomRotationForce, 0.1f, 2.0f);
			Vector3 torque      = new Vector3(
				(float)GD.RandRange(-1, 1),
				(float)GD.RandRange(-1, 1),
				(float)GD.RandRange(-1, 1)
			) * torqueScale;
			die.ApplyTorqueImpulse(torque);
		}

		DiceHUD.AttachDice(_dice);

		// Poll until all dice are sleeping or 15 seconds elapse.
		bool allAtRest    = false;
		int  timeoutTicks = 0;
		while (!allAtRest && timeoutTicks < 150)
		{
			await Task.Delay(100);
			timeoutTicks++;

			allAtRest = true;
			foreach (var die in _dice)
			{
				if (!die.Sleeping && die.LinearVelocity.Length() > 0.01f)
				{
					allAtRest = false;
					break;
				}
			}
		}

		CalculateResults();
		await Task.Delay(2500);
		DiceHUD.HideResult();
		await Task.Delay(450);
		ResetPosition();
	}

	// ── Results ───────────────────────────────────────────────────────────────

	/// <summary>
	/// Reads each die's face-up value and posts the result to the chat log.
	/// </summary>
	private void CalculateResults()
	{
		List<int> values = new List<int>();
		foreach (var die in _dice)
			values.Add(GetDiceValue(die));

		string resultText = $"[color=#ffaa00][DICE][/color] You rolled: {string.Join(", ", values)} (Total: {GetTotal(values)})";
		ChatManager.AddLog(resultText);
	}

	/// <summary>
	/// Determines the face value of a die by finding which local axis best aligns
	/// with world Up, then returning the face value mapped to that axis.
	/// Face mapping: +X=4, -X=3, +Y=2, -Y=5, +Z=6, -Z=1.
	/// </summary>
	private int GetDiceValue(RigidBody3D die)
	{
		Vector3 worldUp = Vector3.Up;
		Basis   b       = die.GlobalTransform.Basis;

		float maxDot = -2.0f;
		int   value  = 0;

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

	// ── Reset ─────────────────────────────────────────────────────────────────

	/// <summary>
	/// Tweens the cup and dice back to their original world transforms,
	/// then re-enables interaction and unlocks the Interactor.
	/// </summary>
	private void ResetPosition()
	{
		_currentState         = State.Resetting;
		_cubileteMesh.Visible = true;

		// Recompute world target from the parent's CURRENT global transform so the
		// cubilete returns to the table surface even after PlayingSetup has risen.
		var parentGlobal   = GetParent<Node3D>().GlobalTransform;
		var resetTransform = parentGlobal * _originalLocalTransform;

		var tween = CreateTween();
		tween.SetParallel(true);
		tween.SetTrans(Tween.TransitionType.Cubic);
		tween.SetEase(Tween.EaseType.Out);

		tween.TweenProperty(this, "global_transform", resetTransform, 1.2f);

		for (int i = 0; i < _dice.Length; i++)
		{
			var die = _dice[i];
			die.Freeze   = true;
			die.Visible  = true;
			// Stack dice slightly above each other to avoid z-fighting on reset.
			tween.TweenProperty(die, "global_position",
				resetTransform.Origin + new Vector3(0, 0.02f + (0.02f * i), 0), 1.2f);
			tween.TweenProperty(die, "quaternion",
				new Quaternion(resetTransform.Basis), 1.2f);
		}

		tween.Finished += () => {
			_currentState                = State.Stationary;
			_interactable.PromptText     = "Grab Cubilete";
			_interactable.ProcessMode    = ProcessModeEnum.Inherit;
			Interactor.IsLocked          = false;

			foreach (var die in _dice)
			{
				die.LinearVelocity  = Vector3.Zero;
				die.AngularVelocity = Vector3.Zero;
			}
		};
	}

	// ── Audio ─────────────────────────────────────────────────────────────────

	/// <summary>
	/// Plays the collision sound on a die scaled to impact velocity.
	/// Ignores micro-movements below a threshold to avoid noise spam.
	/// </summary>
	private void OnDiceCollision(int index, Node body)
	{
		var die         = _dice[index];
		var soundPlayer = die.GetNodeOrNull<AudioStreamPlayer3D>("CollisionSound");
		if (soundPlayer == null) return;

		float velocity = die.LinearVelocity.Length();
		if (velocity < 0.15f) return; // too slow to warrant a sound

		// Map velocity [0, 4] to volume, then apply a -6 dB base-volume offset.
		float intensity = Mathf.Clamp(velocity / 4.0f, 0.0f, 1.0f);
		float volume    = Mathf.LinearToDb(intensity) - 6.0f;

		soundPlayer.PitchScale = (float)GD.RandRange(0.85, 1.15);
		soundPlayer.VolumeDb   = volume;
		soundPlayer.Play();
	}
}
