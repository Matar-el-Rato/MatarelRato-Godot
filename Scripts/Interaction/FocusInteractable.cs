using Godot;
using System;

public partial class FocusInteractable : Node
{
	[Export] public Node3D Target;
	[Export] public Interactable InteractableNode;
	[Export] public float Distance = 0.4f;
	[Export] public Vector3 PositionOffset = Vector3.Zero;
	[Export] public float RotationOffset = 0f;
	[Export] public float TargetFOV = 75.0f;

	public override void _Ready()
	{
		CallDeferred(MethodName.Initialize);
	}

	private void Initialize()
	{
		Target ??= GetParent() as Node3D;
		
		// Look for Interactable node in children or parent
		InteractableNode ??= GetParent().GetNodeOrNull<Interactable>("Interactable");
		InteractableNode ??= GetNodeOrNull<Interactable>("Interactable");

		if (InteractableNode != null)
		{
			InteractableNode.Interacted += OnInteracted;
		}
		else
		{
			GD.PrintErr($"[FocusInteractable] No Interactable node found for {Name} on {GetParent().Name}");
		}
	}

	private void OnInteracted()
	{
		GD.Print($"[FocusInteractable] OnInteracted for {Target?.Name}, sending distance: {Distance}, rot: {RotationOffset}");
		if (FocusController.Instance != null && Target != null)
		{
			FocusController.Instance.FocusOnModular(Target, Distance, PositionOffset, RotationOffset, TargetFOV);
		}
	}
}
