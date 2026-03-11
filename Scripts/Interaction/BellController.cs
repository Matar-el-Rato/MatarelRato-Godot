using Godot;
using System;

public partial class BellController : Node3D
{
	[Export] public AudioStreamPlayer3D BellAudio;
	[Export] public ClipboardController ClipboardCtrl;
	[Export] public NPC TargetNPC;
	[Export] public Interactable InteractableNode;

	public override void _Ready()
	{
		// Fallback: search for nodes if they weren't assigned in the inspector
		InteractableNode ??= GetNodeOrNull<Interactable>("Interactable");
		BellAudio ??= GetNodeOrNull<AudioStreamPlayer3D>("BellAudio");
		ClipboardCtrl ??= GetParent()?.GetNodeOrNull<ClipboardController>("ClipboardController");

		if (InteractableNode != null)
		{
			InteractableNode.Interacted += OnBellInteracted;
			GD.Print($"[BellController] Successfully connected to Interactable: {InteractableNode.Name}");
		}
		
		GD.Print($"[BellController] Ready. TargetNPC: {(TargetNPC != null ? TargetNPC.Name : "Null")}");
	}

	private void OnBellInteracted()
	{
		GD.Print("[BellController] OnBellInteracted RECEIVED!");
		if (BellAudio != null)
		{
			BellAudio.Play();
		}

		if (ClipboardCtrl != null)
		{
			ClipboardCtrl.ShowClipboards();
		}

		GD.Print($"[BellController] Bell rung. Calling Appear on: {(TargetNPC != null ? TargetNPC.Name : "Null")}");
		if (TargetNPC != null)
		{
			TargetNPC.Appear();
		}
	}
}
