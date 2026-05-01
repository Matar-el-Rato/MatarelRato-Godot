// ═══════════════════════════════════════════════════
// MagnifyingGlass.cs
// Interactable magnifying glass prop. Highlights on hover.
// Clicking does nothing for now — peek logic will be
// added once server action support is implemented.
// ═══════════════════════════════════════════════════
using Godot;

public partial class MagnifyingGlass : Node3D
{
	[ExportGroup("Components")]
	[Export] public NodePath InteractablePath;

	private Interactable _interactable;

	public override void _Ready()
	{
		_interactable = GetNodeOrNull<Interactable>(InteractablePath);
		// Interacted signal intentionally not connected — no action yet.
	}
}
