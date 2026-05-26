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
	[Export] public Vector3 HoldOffset        = new Vector3(0, -0.05f, -0.28f);
	/// <summary>World offset from the camera where dice are spawned at throw time.</summary>
	[Export] public Vector3 ThrowOriginOffset = new Vector3(0, -0.3f, -0.5f);
	/// <summary>Camera-local position where the cup mesh floats while held (lower-screen view).</summary>
	[Export] public Vector3 ViewOffset          = new Vector3(0f, -0.22f, -0.38f);
	/// <summary>Rotation (degrees) of the cup in the lower-screen view.</summary>
	[Export] public Vector3 ViewRotationDegrees = new Vector3(0f, 0f, 0f);

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

	private Node3D      _meshOriginalParent;
	private Transform3D _meshOriginalLocalTransform;
	private bool        _meshReparentedToCamera = false;

	private ShaderMaterial           _glowMaterial;
	private List<MeshInstance3D>     _glowShells  = new();
	private bool                     _glowActive  = false;
	private bool                     _glowVisible = false;

	// Completed when ExecuteReturnDiceEarly finishes.
	private System.Threading.Tasks.Task _earlyReturnTask =
		System.Threading.Tasks.Task.CompletedTask;

	// Saved on _Ready so the cup can return after a throw.
	// Stored as LOCAL transform so the reset target is correct even after the
	// parent (PlayingSetup) has been tweened to its final table height.
	private Transform3D _originalLocalTransform;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_cubileteMesh               = GetNode<Node3D>(CubileteMeshPath);
		_meshOriginalParent         = _cubileteMesh.GetParent<Node3D>();
		_meshOriginalLocalTransform = _cubileteMesh.Transform;
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
		_interactable.Enabled     = false;

		CallDeferred(MethodName.FindPlayerCamera);
		CallDeferred(MethodName.InitGlow);
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

	private void InitGlow()
	{
		var shader = GD.Load<Shader>("res://Shaders/outline_vertex.gdshader");
		if (shader == null) { GD.PushWarning("[Cubilete] outline_vertex.gdshader not found"); return; }

		_glowMaterial = new ShaderMaterial { Shader = shader };
		_glowMaterial.SetShaderParameter("outline_color", new Color(1f, 1f, 1f, 0f));
		_glowMaterial.SetShaderParameter("thickness",     0.003f);

		BuildGlowShells(_cubileteMesh);
	}

	private void BuildGlowShells(Node node)
	{
		// Snapshot children BEFORE adding the shell — otherwise we'd recurse into
		// the shell we just added, creating an infinite loop → stack overflow.
		var children = node.GetChildren();

		if (node is MeshInstance3D mi && mi.Mesh != null)
		{
			var shell              = new MeshInstance3D();
			shell.Mesh             = mi.Mesh;
			shell.MaterialOverride = _glowMaterial;
			shell.CastShadow       = GeometryInstance3D.ShadowCastingSetting.Off;
			shell.Transform        = Transform3D.Identity;
			shell.Visible          = false;
			mi.AddChild(shell);
			_glowShells.Add(shell);
		}

		foreach (Node child in children)
			BuildGlowShells(child);
	}

	// ── Input ─────────────────────────────────────────────────────────────────

	public override void _Process(double delta)
	{
		if (_glowMaterial == null) return;
		if (_glowActive != _glowVisible)
		{
			_glowVisible = _glowActive;
			foreach (var shell in _glowShells)
				if (IsInstanceValid(shell)) shell.Visible = _glowVisible;
		}
		if (_glowActive)
		{
			float pulse = 0.55f + 0.45f * Mathf.Sin((float)(Time.GetTicksMsec()) * 0.0025f);
			_glowMaterial.SetShaderParameter("outline_color", new Color(1f, 1f, 1f, pulse));
		}
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

	// ── Turn gating ───────────────────────────────────────────────────────────

	/// <summary>
	/// Called by TableManager on each turn_start to enable interaction only for
	/// the current-turn player.
	/// </summary>
	public void SetInteractionEnabled(bool enabled)
	{
		_interactable.Enabled = enabled;
		_glowActive           = enabled;
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
	private void Grab()
	{
		_glowActive                = false;
		_currentState              = State.Held;
		_interactable.PromptText   = "Roll Dice";
		_interactable.ProcessMode  = ProcessModeEnum.Inherit;
		Interactor.IsLocked        = true;

		SetCubileteVisible(false);
		foreach (var die in _dice)
		{
			die.Freeze  = true;
			die.Visible = false;
		}

		// Tween cup from its resting world position into the lower-screen holding position.
		if (_playerCamera != null)
		{
			Vector3 worldPosBefore        = _cubileteMesh.GlobalPosition;
			_meshReparentedToCamera       = true;
			_cubileteMesh.Reparent(_playerCamera, false);
			_cubileteMesh.Position        = _playerCamera.ToLocal(worldPosBefore);
			_cubileteMesh.RotationDegrees = ViewRotationDegrees;
			_cubileteMesh.Visible         = true;
			CreateTween()
				.TweenProperty(_cubileteMesh, "position", ViewOffset, 0.4f)
				.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
		}
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

		// Wind-up: dip → lunge up → brief hang.  Dice launch as soon as this finishes,
		// concurrent with the slide-out so the cup exits while dice are already mid-air.
		if (_meshReparentedToCamera)
		{
			var windUp = CreateTween();
			windUp.TweenProperty(_cubileteMesh, "position",
				ViewOffset + new Vector3(0f, -0.04f, 0f), 0.13f)
				.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
			windUp.TweenProperty(_cubileteMesh, "position",
				ViewOffset + new Vector3(0f, 0.09f, 0f), 0.10f)
				.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
			windUp.TweenInterval(0.10f);
			await ToSignal(windUp, Tween.SignalName.Finished);
			if (!IsInsideTree()) return;

			// Fire slide-out without awaiting — dice launch runs in parallel.
			// Capture fields for the closure since the tween fires after this frame.
			_meshReparentedToCamera = false;
			var mesh          = _cubileteMesh;
			var origParent    = _meshOriginalParent;
			var origTransform = _meshOriginalLocalTransform;
			var slideOut = CreateTween();
			slideOut.TweenProperty(mesh, "position",
				ViewOffset + new Vector3(0f, -0.18f, 0f), 0.20f)
				.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
			slideOut.TweenCallback(Callable.From(() => {
				mesh.Visible = false;
				mesh.Reparent(origParent, false);
				mesh.Transform = origTransform;
			}));
		}
		RestoreMeshToParent(); // no-op when _meshReparentedToCamera was cleared above

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

	// ── Doubles reroll ────────────────────────────────────────────────────────

	/// <summary>
	/// Called from TableManager when dice_result carries is_doubles=true (local player only).
	/// Waits 1 s, then smoothly returns the cup and dice to <paramref name="boardPos"/> and
	/// readies the cup — so the board is clear before the player makes their piece move.
	/// </summary>
	public void ReturnDiceEarly(Vector3 boardPos)
	{
		var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
		_earlyReturnTask = tcs.Task;
		ExecuteReturnDiceEarly(boardPos, tcs);
	}

	private async void ExecuteReturnDiceEarly(
		Vector3 boardPos, System.Threading.Tasks.TaskCompletionSource<bool> tcs)
	{
		await ToSignal(GetTree().CreateTimer(1.0f), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) { tcs.TrySetResult(true); return; }

		RestoreMeshToParent();
		SetCubileteVisible(true);
		GlobalRotation = Vector3.Zero;
		var returnTween = CreateTween().SetParallel(true)
			.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
		returnTween.TweenProperty(this, "global_position", boardPos, 0.45f);
		for (int i = 0; i < _dice.Length; i++)
			returnTween.TweenProperty(_dice[i], "global_position",
				boardPos + Vector3.Up * (0.02f + 0.02f * i), 0.45f);
		await ToSignal(returnTween, Tween.SignalName.Finished);
		if (!IsInsideTree()) { tcs.TrySetResult(true); return; }

		foreach (var die in _dice) { die.Freeze = true; die.Visible = false; }
		_currentState             = State.Stationary;
		_interactable.ProcessMode = ProcessModeEnum.Inherit;
		_interactable.PromptText  = "Grab Cubilete";
		_glowActive               = true;
		tcs.SetResult(true);
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
		RestoreMeshToParent();
		SetCubileteVisible(true);
		_currentState             = State.Stationary;
		_interactable.ProcessMode = ProcessModeEnum.Inherit;
		_interactable.PromptText  = "Grab Cubilete";
		_glowActive               = true;
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
	/// Cosmetic-only throw animation for remote players.
	/// No physics — tween-driven cup shake + dice arc so there's no conflict
	/// with server-authoritative results. State does not change.
	/// </summary>
	public async void PlayRemoteThrow(float throwDuration = 1.0f, bool withDiceArc = true)
	{
		if (_currentState == State.Hidden) return;

		// Wait for the cup to finish arcing to this player's position if needed.
		int waited = 0;
		while (_currentState == State.Moving && waited < 25)
		{
			await ToSignal(GetTree().CreateTimer(0.1f), SceneTreeTimer.SignalName.Timeout);
			if (!IsInsideTree()) return;
			waited++;
		}
		if (_currentState == State.Hidden) return;

		Vector3 origin = GlobalPosition;

		// ── Cup shake ─────────────────────────────────────────────────────────
		var shake = CreateTween();
		shake.TweenProperty(this, "rotation:z",  Mathf.DegToRad( 7f), 0.055f);
		shake.TweenProperty(this, "rotation:z",  Mathf.DegToRad(-7f), 0.110f);
		shake.TweenProperty(this, "rotation:z",  Mathf.DegToRad( 5f), 0.080f);
		shake.TweenProperty(this, "rotation:z",  Mathf.DegToRad(-5f), 0.090f);
		shake.TweenProperty(this, "rotation:z",  0f,                   0.065f);
		await ToSignal(shake, Tween.SignalName.Finished);
		if (!IsInsideTree()) return;

		if (!withDiceArc) return;

		PlayClack(pitch: 1.05f);

		// ── Dice arc (tween only, frozen RigidBodies) ─────────────────────────
		float arcTime = throwDuration * 0.85f;
		for (int i = 0; i < _dice.Length; i++)
		{
			float side = i == 0 ? -1f : 1f;
			var   die  = _dice[i];

			die.Freeze         = true;
			die.GlobalPosition = origin + new Vector3(side * 0.04f, 0.02f, 0f);
			die.Visible        = true;

			Vector3 peak = origin + new Vector3(side * 0.10f, 0.30f, 0.08f);

			// Position: arc up then back to origin
			var posTween = die.CreateTween();
			posTween.TweenProperty(die, "global_position", peak,
				arcTime * 0.45f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
			posTween.TweenProperty(die, "global_position",
				origin + new Vector3(side * 0.04f, 0.02f, 0f),
				arcTime * 0.55f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);

			// Rotation: tumble across all three axes
			var rotTarget = die.GlobalRotation + new Vector3(
				(float)GD.RandRange(-1f, 1f),
				(float)GD.RandRange(-1f, 1f),
				(float)GD.RandRange(-1f, 1f)) * Mathf.Tau * 1.5f;
			die.CreateTween()
				.TweenProperty(die, "global_rotation", rotTarget, arcTime)
				.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
		}

		await ToSignal(GetTree().CreateTimer(throwDuration), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) return;

		PlayClack(pitch: 0.95f);
		for (int i = 0; i < _dice.Length; i++)
		{
			_dice[i].GlobalPosition  = origin + new Vector3(0f, 0.02f + 0.02f * i, 0f);
			_dice[i].Visible         = false;
		}
	}

	// ── Visibility + collision ────────────────────────────────────────────────

	private void RestoreMeshToParent()
	{
		if (!_meshReparentedToCamera) return;
		_meshReparentedToCamera = false;
		_cubileteMesh.Visible   = false;
		_cubileteMesh.Reparent(_meshOriginalParent, false);
		_cubileteMesh.Transform = _meshOriginalLocalTransform;
	}

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
		RestoreMeshToParent();
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

	private void PlayClack(float pitch = 1.0f)
	{
		var clip = GD.Load<AudioStream>("res://Assets/Sound FX/clack.wav");
		if (clip == null) return;
		var sfx = new AudioStreamPlayer { Stream = clip, VolumeDb = -6f, PitchScale = pitch };
		AddChild(sfx);
		sfx.Play();
		sfx.Finished += () => sfx.QueueFree();
	}

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
