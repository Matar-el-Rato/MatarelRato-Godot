// ═══════════════════════════════════════════════════
// Selector.cs
// Cycles through an array of CharacterEntry resources
// and calls SwapCharacter on the PlayerCameraController.
// Bound to the "cycle_character" action or the P key.
// ═══════════════════════════════════════════════════
using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Attached near the player. Holds an ordered list of <see cref="CharacterEntry"/>
/// resources and cycles through them on the "cycle_character" action (or P key),
/// delegating the actual swap to <see cref="PlayerCameraController.SwapCharacter"/>.
/// </summary>
public partial class Selector : Node
{
	/// <summary>Ordered list of character presets to cycle through.</summary>
	[Export] public CharacterEntry[] Entries;
	[Export] public NodePath PlayerControllerPath = "..";

	private PlayerCameraController _playerController;
	private int _currentIndex = 0;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		if (PlayerControllerPath != null)
			_playerController = GetNodeOrNull<PlayerCameraController>(PlayerControllerPath);

		// Apply the first entry immediately so the player has a model from the start.
		if (_playerController != null && Entries != null && Entries.Length > 0)
			_playerController.SwapCharacter(Entries[0]);
	}

	// ── Input ─────────────────────────────────────────────────────────────────

	public override void _UnhandledInput(InputEvent @event)
	{
		bool actionPressed = false;
		if (InputMap.HasAction("cycle_character"))
			actionPressed = @event.IsActionPressed("cycle_character");

		// Also accept P as a hardcoded fallback in case the action isn't mapped.
		if (actionPressed || (@event is InputEventKey ek && ek.Pressed && ek.Keycode == Key.P))
			CycleCharacter();
	}

	// ── Cycling ───────────────────────────────────────────────────────────────

	private void CycleCharacter()
	{
		if (Entries == null || Entries.Length == 0)
		{
			GD.PrintErr("Selector Error: Entries list is empty or NULL.");
			return;
		}
		if (_playerController == null)
		{
			GD.PrintErr("Selector Error: PlayerController is NULL.");
			return;
		}

		_currentIndex = (_currentIndex + 1) % Entries.Length;
		var entry = Entries[_currentIndex];

		if (entry?.ModelScene == null)
		{
			GD.PrintErr($"Selector Error: Character at index {_currentIndex} has no ModelScene.");
			return;
		}

		_playerController.SwapCharacter(entry);
	}
}
