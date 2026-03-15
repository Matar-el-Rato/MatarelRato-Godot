// ═══════════════════════════════════════════════════
// IInteractable.cs
// Contract that every interactable world object must satisfy.
// ═══════════════════════════════════════════════════
using Godot;

/// <summary>
/// Minimum interface for anything the player can interact with.
/// Implemented by <see cref="Interactable"/> and any custom interactable node.
/// </summary>
public interface IInteractable
{
	/// <summary>Called when the player commits an interaction (key press or click).</summary>
	void Interact();

	/// <summary>Called when the player's raycast first lands on this object.</summary>
	void OnFocus();

	/// <summary>Called when the player's raycast leaves this object.</summary>
	void OnBlur();
}
