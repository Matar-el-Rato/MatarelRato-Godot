// ═══════════════════════════════════════════════════
// PlayerItemSet.cs
// Manages ownership of a per-player item set on the table.
// All items start non-interactable. When the local player
// sits in the matching chair, SetOwned(true) is called and
// interaction is enabled only for this set.
// ═══════════════════════════════════════════════════
using Godot;

/// <summary>
/// Attached to each PlayerItemSet instance spawned by TableManager.
/// Disables ray-pickability and interaction on all child Interactable nodes
/// until the matching chair is claimed by the local player.
/// </summary>
public partial class PlayerItemSet : Node3D
{
	/// <summary>Slot index assigned by TableManager (0=front/blue … 3=right/red).</summary>
	[Export] public int SlotIndex = -1;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		// Defer one frame so child Interactable nodes finish generating
		// their trimesh collision shapes before we disable ray-pickability.
		CallDeferred(MethodName.DisableItems);
	}

	private void DisableItems() => SetOwned(false);

	// ── Public API ────────────────────────────────────────────────────────────

	/// <summary>
	/// Enables or disables all interactable items in this set.
	/// Called with true when the local player sits in the matching chair.
	/// </summary>
	public void SetOwned(bool owned)
	{
		SetChildInteractions(this, owned);
	}

	// ── Helpers ───────────────────────────────────────────────────────────────

	private void SetChildInteractions(Node node, bool enabled)
	{
		if (node is CollisionObject3D col)
			col.InputRayPickable = enabled;

		if (node is Interactable interactable)
			interactable.Enabled = enabled;

		foreach (Node child in node.GetChildren())
			SetChildInteractions(child, enabled);
	}
}
