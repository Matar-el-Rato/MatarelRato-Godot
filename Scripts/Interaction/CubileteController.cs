// ═══════════════════════════════════════════════════
// CubileteController.cs
// Controls the cubilete (dice cup) prop: grab animation,
// physics dice throw, result calculation, and automatic reset.
// ═══════════════════════════════════════════════════
using Godot;
using System;
using System.Collections.Generic;

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

	// ── Signals ───────────────────────────────────────────────────────────────
	/// <summary>Emitted after dice results are displayed, carrying the face values of each die.</summary>
	[Signal] public delegate void RollCompletedEventHandler(int die1, int die2);

	[ExportGroup("Turn Arc")]
	/// <summary>How far below the table the cup starts (hidden until the starter is chosen).</summary>
	[Export] public float HiddenDepth = 0.175f;
	/// <summary>Arc height above surface during the inter-player transit.</summary>
	[Export] public float ArcBobHeight = 0.22f;
	/// <summary>Duration of the arc transit in seconds.</summary>
	[Export] public float ArcDuration  = 1.6f;

	private enum State { Hidden, Stationary, Held, Rolling, Resetting, Moving }
	private State _currentState = State.Hidden;

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

		// Start hidden below the table surface.
		Position = new Vector3(Position.X, Position.Y - HiddenDepth, Position.Z);
		SetCubileteVisible(false);
		foreach (var die in _dice) die.Visible = false;
		_interactable.ProcessMode = ProcessModeEnum.Disabled;

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
		SetCubileteVisible(false);
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

		// Poll until all dice are truly still (both linear AND angular velocity below
		// threshold) or 15 s elapse.  Checking only LinearVelocity misses slow spins.
		bool allAtRest    = false;
		int  timeoutTicks = 0;
		while (!allAtRest && timeoutTicks < 150)
		{
			await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
			if (!IsInsideTree()) return;
			timeoutTicks++;

			allAtRest = true;
			foreach (var die in _dice)
			{
				if (die.LinearVelocity.Length() > 0.01f || die.AngularVelocity.Length() > 0.05f)
				{
					allAtRest = false;
					break;
				}
			}
		}

		// Freeze FIRST so no additional physics step can change orientation between
		// the read and the freeze.
		foreach (var die in _dice) die.Freeze = true;

		// Now read face values from the frozen (authoritative) transforms.
		var (die1, die2) = CalculateResults();

		// Lock HUD to the frozen face values — stops live transform tracking so
		// MoveToPlayer repositioning the dice can't corrupt the display.
		DiceHUD.ShowStatic(die1, die2);
		Interactor.IsLocked = false;
		_currentState = State.Moving;
		EmitSignal(SignalName.RollCompleted, die1, die2);

		// HUD fades out in the background while the cup is already moving.
		await ToSignal(GetTree().CreateTimer(2.5), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) return;
		DiceHUD.HideResult();
		await ToSignal(GetTree().CreateTimer(0.45), SceneTreeTimer.SignalName.Timeout);
	}

	// ── Results ───────────────────────────────────────────────────────────────

	/// <summary>
	/// Reads each die's face-up value, posts the result to the chat log, and returns (die1, die2).
	/// </summary>
	private (int die1, int die2) CalculateResults()
	{
		var values = new List<int>();
		foreach (var die in _dice)
			values.Add(GetDiceValue(die));

		string resultText = $"[color=#ffaa00][DICE][/color] You rolled: {string.Join(", ", values)} (Total: {GetTotal(values)})";
		ChatManager.AddLog(resultText);

		return (values.Count > 0 ? values[0] : 0, values.Count > 1 ? values[1] : 0);
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

	// ── Turn transitions ──────────────────────────────────────────────────────

	/// <summary>
	/// Rise the cup up from its hidden position to <paramref name="surfaceWorldPos"/>.
	/// Called by TableManager when the starter is determined.
	/// </summary>
	public async void AppearAt(Vector3 surfaceWorldPos)
	{
		if (_currentState != State.Hidden && _currentState != State.Moving) return;

		SetCubileteVisible(false);
		_interactable.ProcessMode = ProcessModeEnum.Disabled;

		// Teleport to below-surface then rise — snap flat before showing mesh.
		GlobalPosition = new Vector3(surfaceWorldPos.X, surfaceWorldPos.Y - HiddenDepth, surfaceWorldPos.Z);
		GlobalRotation = Vector3.Zero;
		SetCubileteVisible(true);

		var tween = CreateTween()
			.SetTrans(Tween.TransitionType.Back)
			.SetEase(Tween.EaseType.Out);
		tween.TweenProperty(this, "global_position", surfaceWorldPos, 0.85f);
		await ToSignal(tween, Tween.SignalName.Finished);
		if (!IsInsideTree()) return;

		_currentState             = State.Stationary;
		_interactable.ProcessMode = ProcessModeEnum.Inherit;
		_interactable.PromptText  = "Grab Cubilete";
	}

	/// <summary>
	/// Resets the cup to Stationary in place, ready for another grab+roll.
	/// Called on a doubles extra turn when the cup stays at the current player's position.
	/// </summary>
	public void ReadyForRoll()
	{
		foreach (var die in _dice) { die.Freeze = true; die.Visible = false; }
		GlobalRotation = Vector3.Zero;
		SetCubileteVisible(true);
		_currentState             = State.Stationary;
		_interactable.ProcessMode = ProcessModeEnum.Inherit;
		_interactable.PromptText  = "Grab Cubilete";
	}

	/// <summary>
	/// Arc the cup clockwise around <paramref name="boardCenter"/> to <paramref name="targetSurfacePos"/>,
	/// then settle and enable interaction. Dice are hidden during transit and re-parked on arrival.
	/// </summary>
	public async void MoveToPlayer(Vector3 targetSurfacePos, Vector3 boardCenter)
	{
		_currentState             = State.Moving;
		_interactable.ProcessMode = ProcessModeEnum.Disabled;

		// Hide dice during transit; snap cup flat immediately (not visible while tilted is jarring).
		foreach (var die in _dice) { die.Freeze = true; die.Visible = false; }
		GlobalRotation = Vector3.Zero;
		SetCubileteVisible(true);

		// ── Compute arc ───────────────────────────────────────────────────────
		var startOffset = new Vector2(GlobalPosition.X - boardCenter.X, GlobalPosition.Z - boardCenter.Z);
		var endOffset   = new Vector2(targetSurfacePos.X - boardCenter.X, targetSurfacePos.Z - boardCenter.Z);

		float radius     = Mathf.Max(startOffset.Length(), 0.01f);
		float startAngle = Mathf.Atan2(startOffset.Y, startOffset.X);
		float endAngle   = Mathf.Atan2(endOffset.Y,   endOffset.X);

		// Always travel clockwise (increasing angle in Godot XZ convention).
		while (endAngle <= startAngle) endAngle += Mathf.Tau;

		float surfaceY = targetSurfacePos.Y;
		int   steps    = 24;

		var arcTween = CreateTween().SetTrans(Tween.TransitionType.Linear);
		for (int i = 1; i <= steps; i++)
		{
			float t     = (float)i / steps;
			float angle = Mathf.Lerp(startAngle, endAngle, t);
			float x     = boardCenter.X + radius * Mathf.Cos(angle);
			float z     = boardCenter.Z + radius * Mathf.Sin(angle);
			float y     = surfaceY + Mathf.Sin(t * Mathf.Pi) * ArcBobHeight;
			arcTween.TweenProperty(this, "global_position", new Vector3(x, y, z), ArcDuration / steps);
		}
		await ToSignal(arcTween, Tween.SignalName.Finished);
		if (!IsInsideTree()) return;

		// Snap to exact target position + flatten rotation with a small bounce.
		var settleTween = CreateTween().SetParallel()
			.SetTrans(Tween.TransitionType.Back)
			.SetEase(Tween.EaseType.Out);
		settleTween.TweenProperty(this, "global_position", targetSurfacePos, 0.3f);
		settleTween.TweenProperty(this, "global_rotation", Vector3.Zero, 0.3f);
		await ToSignal(settleTween, Tween.SignalName.Finished);
		if (!IsInsideTree()) return;

		// Park dice inside the cup at the new position.
		for (int i = 0; i < _dice.Length; i++)
		{
			_dice[i].GlobalPosition = targetSurfacePos + new Vector3(0f, 0.02f + 0.02f * i, 0f);
			_dice[i].GlobalRotation = GlobalRotation;
			_dice[i].LinearVelocity  = Vector3.Zero;
			_dice[i].AngularVelocity = Vector3.Zero;
			_dice[i].Visible         = true;
		}

		_currentState             = State.Stationary;
		_interactable.ProcessMode = ProcessModeEnum.Inherit;
		_interactable.PromptText  = "Grab Cubilete";
	}

	/// <summary>
	/// Cosmetic throw for remote players: briefly launches the local dice with random impulses,
	/// then snaps them back inside the cup after <paramref name="throwDuration"/> seconds.
	/// Does not change state — the cup stays Stationary for the local player.
	/// </summary>
	public async void PlayRemoteThrow(float throwDuration = 1.0f)
	{
		if (_currentState == State.Hidden) return;

		// If the cubilete is still arcing to this player's position, wait for it to settle.
		// This happens when a remote player rolls immediately after their turn_start fires.
		int waited = 0;
		while (_currentState == State.Moving && waited < 25)
		{
			await ToSignal(GetTree().CreateTimer(0.1f), SceneTreeTimer.SignalName.Timeout);
			if (!IsInsideTree()) return;
			waited++;
		}
		if (_currentState == State.Hidden) return;

		Vector3 launchPos = GlobalPosition;
		for (int i = 0; i < _dice.Length; i++)
		{
			_dice[i].Freeze          = false;
			_dice[i].LinearVelocity  = Vector3.Zero;
			_dice[i].AngularVelocity = Vector3.Zero;
			_dice[i].GlobalPosition  = launchPos + new Vector3(
				(float)GD.RandRange(-0.06f, 0.06f), 0.04f + 0.04f * i, (float)GD.RandRange(-0.06f, 0.06f));
			_dice[i].Visible = true;
			_dice[i].ApplyCentralImpulse(new Vector3(
				(float)GD.RandRange(-0.5f, 0.5f),
				(float)GD.RandRange(1.8f,  2.8f),
				(float)GD.RandRange(-0.5f, 0.5f)) * ThrowForce);
			_dice[i].ApplyTorqueImpulse(new Vector3(
				(float)GD.RandRange(-1f, 1f),
				(float)GD.RandRange(-1f, 1f),
				(float)GD.RandRange(-1f, 1f)) * RandomRotationForce);
		}

		await ToSignal(GetTree().CreateTimer(throwDuration), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) return;

		for (int i = 0; i < _dice.Length; i++)
		{
			_dice[i].Freeze          = true;
			_dice[i].LinearVelocity  = Vector3.Zero;
			_dice[i].AngularVelocity = Vector3.Zero;
			_dice[i].GlobalPosition  = launchPos + new Vector3(0f, 0.02f + 0.02f * i, 0f);
			_dice[i].Visible         = false;
		}
	}

	// ── Visibility + collision ────────────────────────────────────────────────

	private void SetCubileteVisible(bool visible)
	{
		_cubileteMesh.Visible = visible;
		// Sync collision layer so hidden cup doesn't block 3-D raycasts (e.g. piece clicks).
		foreach (var child in _cubileteMesh.FindChildren("*", "", true, false))
		{
			if (child is CollisionObject3D co)
			{
				co.CollisionLayer = visible ? 1u : 0u;
				co.CollisionMask  = visible ? 1u : 0u;
			}
		}
	}

	// ── Reset ─────────────────────────────────────────────────────────────────

	/// <summary>
	/// Tweens the cup and dice back to their original world transforms,
	/// then re-enables interaction and unlocks the Interactor.
	/// </summary>
	private void ResetPosition()
	{
		_currentState = State.Resetting;
		SetCubileteVisible(true);

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
