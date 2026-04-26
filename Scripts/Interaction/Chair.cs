// ═══════════════════════════════════════════════════
// Chair.cs
// Makes a chair prop interactable: triggers the player's
// Sit() animation and camera transition when clicked.
// ═══════════════════════════════════════════════════
using Godot;
using System;

/// <summary>
/// Attached to chair props. On interaction, locates the
/// <see cref="PlayerCameraController"/> and calls <see cref="PlayerCameraController.Sit"/>.
/// Uses the "player" group for a fast lookup, with a recursive tree search as fallback.
/// </summary>
public partial class Chair : Node3D
{
	/// <summary>Local-space offset from the chair origin where the player body is placed.</summary>
	[Export] public Vector3 SitOffset = new Vector3(0, 0.45f, 0.15f);
	/// <summary>Camera field-of-view while the player is seated.</summary>
	[Export] public float   SitFOV    = 110f;

	private Interactable _interactable;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		// Try the named child first, then fall back to any Interactable in children.
		_interactable = GetNodeOrNull<Interactable>("Interactable");
		if (_interactable == null)
		{
			foreach (var child in GetChildren())
			{
				if (child is Interactable i)
				{
					_interactable = i;
					break;
				}
			}
		}

		if (_interactable != null)
		{
			_interactable.Interacted   += OnInteracted;
			_interactable.UseLeftClick  = true;
			_interactable.PromptText    = "Sit";
		}
		else
		{
			GD.PrintErr($"[Chair] {Name} could not find an Interactable child!");
		}
	}

	// ── Public API ───────────────────────────────────────────────────────────

	/// <summary>
	/// Sets the chair's highlight and prompt colors for seat-selection.
	/// Call after AddChild so _interactable is resolved.
	/// </summary>
	public void SetSlotColor(Color color, string colorName)
	{
		if (_interactable == null) return;
		_interactable.HighlightColor = color;
		_interactable.PromptColor    = color;
		_interactable.PromptText     = $"Pick \"{colorName}\"";
	}

	// ── Handler ───────────────────────────────────────────────────────────────

	private void OnInteracted()
	{
		var player = GetTree().GetFirstNodeInGroup("player") as PlayerCameraController;

		// Fallback: recursive tree walk if the group lookup returns null.
		if (player == null)
			player = FindPlayerRecursive(GetTree().Root);

		player?.Sit(this);
	}

	// ── Helpers ───────────────────────────────────────────────────────────────

	/// <summary>
	/// Depth-first recursive search for a <see cref="PlayerCameraController"/> anywhere in the tree.
	/// </summary>
	private PlayerCameraController FindPlayerRecursive(Node node)
	{
		if (node is PlayerCameraController p) return p;
		foreach (Node child in node.GetChildren())
		{
			var found = FindPlayerRecursive(child);
			if (found != null) return found;
		}
		return null;
	}
}
