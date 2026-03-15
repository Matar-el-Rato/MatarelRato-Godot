// ═══════════════════════════════════════════════════
// FocusInteractable.cs
// Helper component that triggers FocusController.FocusOnModular
// when its sibling Interactable is activated.
// Drop it alongside an Interactable to make any prop focusable.
// ═══════════════════════════════════════════════════
using Godot;
using System;

/// <summary>
/// Bridges an <see cref="Interactable"/> signal to <see cref="FocusController.FocusOnModular"/>.
/// By default targets its own parent Node3D; override <see cref="Target"/> in the editor
/// to point at a different surface.
/// </summary>
public partial class FocusInteractable : Node
{
	/// <summary>The Node3D that the camera will focus on. Defaults to the parent node.</summary>
	[Export] public Node3D       Target;
	[Export] public Interactable InteractableNode;
	/// <summary>Distance in metres between the camera and the target surface while focused.</summary>
	[Export] public float        Distance        = 0.4f;
	[Export] public Vector3      PositionOffset  = Vector3.Zero;
	/// <summary>Roll offset in degrees around the surface normal for non-upright props.</summary>
	[Export] public float        RotationOffset  = 0f;
	[Export] public float        TargetFOV       = 75.0f;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		CallDeferred(MethodName.Initialize);
	}

	private void Initialize()
	{
		Target ??= GetParent() as Node3D;

		// Look for an Interactable on the parent or as a direct child.
		InteractableNode ??= GetParent().GetNodeOrNull<Interactable>("Interactable");
		InteractableNode ??= GetNodeOrNull<Interactable>("Interactable");

		if (InteractableNode != null)
			InteractableNode.Interacted += OnInteracted;
		else
			GD.PrintErr($"[FocusInteractable] No Interactable node found for {Name} on {GetParent().Name}");
	}

	// ── Handler ───────────────────────────────────────────────────────────────

	private void OnInteracted()
	{
		if (FocusController.Instance != null && Target != null)
			FocusController.Instance.FocusOnModular(Target, Distance, PositionOffset, RotationOffset, TargetFOV);
	}
}
