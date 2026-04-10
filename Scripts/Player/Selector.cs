// ═══════════════════════════════════════════════════
// Selector.cs
// Holds the ordered list of CharacterEntry resources and
// applies the default skin (index 0) on _Ready.
// Skin switching is handled by the CharacterSelector scene.
// ═══════════════════════════════════════════════════
using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Attached to the player. Holds the exported list of <see cref="CharacterEntry"/>
/// resources and applies the first entry as the default skin on startup.
/// The <see cref="CharacterSelector"/> scene reads <see cref="Entries"/> to
/// resolve per-character camera offsets when the player picks a skin.
/// </summary>
public partial class Selector : Node
{
	/// <summary>Ordered list of character presets to cycle through.</summary>
	[Export] public CharacterEntry[] Entries;
	[Export] public NodePath PlayerControllerPath = "..";

	private PlayerCameraController _playerController;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		if (PlayerControllerPath != null)
			_playerController = GetNodeOrNull<PlayerCameraController>(PlayerControllerPath);

		if (_playerController == null)
			_playerController = GetTree().Root.FindChild("Player", true, false)
									as PlayerCameraController;

		// Apply the first entry immediately so the player has a model from the start.
		if (_playerController != null && Entries != null && Entries.Length > 0)
			_playerController.SwapCharacter(Entries[0]);
	}

}
