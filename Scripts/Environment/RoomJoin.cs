// ═══════════════════════════════════════════════════
// RoomJoin.cs
// Manages room entry/exit for a private room.
// On entry: each interior light flickers on independently.
// On exit:  each light flickers off independently, then doors close.
// Also checks player proximity to trigger the room's standalone NPC.
// ═══════════════════════════════════════════════════
using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Attach to a Node3D inside a room. Wire up exports in the editor:
/// <list type="bullet">
/// <item><see cref="LightingNode"/> — Node3D whose Light3D descendants are the interior lights.</item>
/// <item><see cref="RoomArea"/>    — Area3D covering the room interior (player enter/exit detection).</item>
/// <item><see cref="Player"/>      — The player CharacterBody3D.</item>
/// <item><see cref="RoomNpc"/>     — The room's standalone <see cref="RoomNPC"/> instance.</item>
/// <item><see cref="LeftDoorPath"/> / <see cref="RightDoorPath"/> — Paths to the door pair.</item>
/// </list>
/// </summary>
public partial class RoomJoin : Node3D
{
	[Export] public NodePath        LeftDoorPath;
	[Export] public NodePath        RightDoorPath;
	[Export] public Node3D          LightingNode;
	[Export] public Area3D          RoomArea;
	[Export] public CharacterBody3D Player;
	[Export] public RoomNPC         RoomNpc;
	[Export] public AudioStream     FlickerSound;
	/// <summary>Apparel props that slide open on welcome and slide back on room exit.</summary>
	[Export] public Node3D          LeftApparel;
	[Export] public Node3D          RightApparel;

	[ExportGroup("Tuning")]
	[Export] public float NpcProximityDistance = 4.0f;
	[Export] public float NpcHysteresis        = 1.5f;
	/// <summary>Delay after lights finish flickering off before the doors close.</summary>
	[Export] public float DoorCloseDelay       = 2.0f;

	private Door _leftDoor;
	private Door _rightDoor;
	private readonly List<(Light3D light, float originalEnergy)> _lights = new();
	private bool  _playerInside        = false;
	private AudioStreamPlayer _flickerAudio;
	private float _leftApparelBaseZ;
	private float _rightApparelBaseZ;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_leftDoor  = LeftDoorPath  is { IsEmpty: false } ? GetNodeOrNull<Door>(LeftDoorPath)  : null;
		_rightDoor = RightDoorPath is { IsEmpty: false } ? GetNodeOrNull<Door>(RightDoorPath) : null;

		FlickerSound ??= ResourceLoader.Load<AudioStream>("res://Assets/Sound FX/light_flicker.wav");
		if (FlickerSound != null)
		{
			_flickerAudio        = new AudioStreamPlayer();
			_flickerAudio.Stream = FlickerSound;
			AddChild(_flickerAudio);
		}

		if (LeftApparel == null)
			LeftApparel  = GetTree().Root.FindChild("left_apparel",  true, false) as Node3D;
		if (RightApparel == null)
			RightApparel = GetTree().Root.FindChild("right_apparel", true, false) as Node3D;

		if (LeftApparel  != null) _leftApparelBaseZ  = LeftApparel.Position.Z;
		if (RightApparel != null) _rightApparelBaseZ = RightApparel.Position.Z;

		if (LightingNode != null)
		{
			CollectLights(LightingNode);
			// Start all lights off — they flicker on when the player enters.
			foreach (var (light, _) in _lights)
				if (IsInstanceValid(light)) light.LightEnergy = 0f;
		}

		if (RoomArea != null)
		{
			RoomArea.CollisionMask = 0xFFFFFFFF;
			RoomArea.Monitoring    = true;
		}
	}

	// ── Per-frame checks ──────────────────────────────────────────────────────

	public override void _Process(double delta)
	{
		if (Player == null) return;

		// NPC proximity
		if (RoomNpc != null)
		{
			float dist = RoomNpc.GlobalPosition.DistanceTo(Player.GlobalPosition);
			if (!RoomNpc.HasAppeared && dist <= NpcProximityDistance)
				RoomNpc.Appear();
			else if (RoomNpc.HasAppeared && dist > NpcProximityDistance + NpcHysteresis)
				RoomNpc.Disappear();
		}

		// Polled area check — avoids physics layer mismatch issues with signals.
		// Skip while the player is sitting: Sit() disables the collision shape,
		// which makes OverlapsBody return false even though the player hasn't moved.
		if (RoomArea != null && !(Player is PlayerCameraController { IsSitting: true }))
		{
			bool inside = RoomArea.OverlapsBody(Player);
			if (inside && !_playerInside)
			{
				_playerInside = true;
				StartFlickerOn();
			}
			else if (!inside && _playerInside)
			{
				_playerInside = false;
				StartFlickerOff();
			}
		}
	}

	// ── Flicker orchestration ─────────────────────────────────────────────────

	private void StartFlickerOn()
	{
		if (_lights.Count == 0) return;
		_flickerAudio?.Play();
		foreach (var (light, origEnergy) in _lights)
			FlickerLightOn(light, origEnergy);
	}

	private void StartFlickerOff()
	{
		_flickerAudio?.Play();
		foreach (var (light, origEnergy) in _lights)
			FlickerLightOff(light, origEnergy);
		CloseDoorAfterDelay();
		ResetApparel();
		RoomNpc?.ResetWelcome();
	}

	private void ResetApparel()
	{
		if (LeftApparel == null && RightApparel == null) return;
		var tween = CreateTween();
		tween.SetParallel(true);
		if (LeftApparel != null)
			tween.TweenProperty(LeftApparel,  "position:z", _leftApparelBaseZ,  1.0f)
				 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
		if (RightApparel != null)
			tween.TweenProperty(RightApparel, "position:z", _rightApparelBaseZ, 1.0f)
				 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
	}

	// ── Per-light flicker coroutines ──────────────────────────────────────────

	/// <summary>
	/// Flickers a single light on with a random start delay and independent timing,
	/// simulating a fluorescent tube struggling to start.
	/// </summary>
	private async void FlickerLightOn(Light3D light, float origEnergy)
	{
		var rng = new Random();

		// Stagger start so lights don't all trigger at the same moment.
		float startDelay = (float)rng.NextDouble() * 0.45f;
		await ToSignal(GetTree().CreateTimer(startDelay), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree() || !IsInstanceValid(light)) return;

		int cycles = 2 + rng.Next(5); // 2–6 individual flicker pulses
		for (int i = 0; i < cycles; i++)
		{
			if (!IsInsideTree() || !IsInstanceValid(light)) return;
			// Partial brightness flicker — not always full power.
			float partial = 0.35f + (float)rng.NextDouble() * 0.65f;
			light.LightEnergy = (i % 2 == 0) ? 0f : origEnergy * partial;
			float wait = 0.03f + (float)rng.NextDouble() * 0.13f;
			await ToSignal(GetTree().CreateTimer(wait), SceneTreeTimer.SignalName.Timeout);
		}

		if (!IsInsideTree() || !IsInstanceValid(light)) return;
		light.LightEnergy = origEnergy;
	}

	/// <summary>
	/// Flickers a single light off with random stagger and independent timing,
	/// then leaves it fully off.
	/// </summary>
	private async void FlickerLightOff(Light3D light, float origEnergy)
	{
		var rng = new Random();

		float startDelay = (float)rng.NextDouble() * 0.45f;
		await ToSignal(GetTree().CreateTimer(startDelay), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree() || !IsInstanceValid(light)) return;

		int cycles = 2 + rng.Next(4); // 2–5 flicker pulses before dying
		for (int i = 0; i < cycles; i++)
		{
			if (!IsInsideTree() || !IsInstanceValid(light)) return;
			float partial = 0.35f + (float)rng.NextDouble() * 0.65f;
			light.LightEnergy = (i % 2 == 0) ? origEnergy * partial : 0f;
			float wait = 0.03f + (float)rng.NextDouble() * 0.13f;
			await ToSignal(GetTree().CreateTimer(wait), SceneTreeTimer.SignalName.Timeout);
		}

		if (!IsInsideTree() || !IsInstanceValid(light)) return;
		light.LightEnergy = 0f;
	}

	private async void CloseDoorAfterDelay()
	{
		await ToSignal(GetTree().CreateTimer(DoorCloseDelay), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) return;
		_leftDoor?.Close();
		_rightDoor?.Close();
	}

	// ── Helpers ───────────────────────────────────────────────────────────────

	private void CollectLights(Node node)
	{
		if (node is Light3D light)
			_lights.Add((light, light.LightEnergy));
		foreach (Node child in node.GetChildren())
			CollectLights(child);
	}
}
