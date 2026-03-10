using Godot;
using System;

public partial class BellController : Node3D
{
	[Export] public AudioStreamPlayer3D BellAudio;
	[Export] public ClipboardController ClipboardCtrl;
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
		}
		else
		{
			GD.PushWarning($"[BellController] {Name} couldn't find InteractableNode!");
		}
	}

	private void OnBellInteracted()
	{
		if (BellAudio != null)
		{
			BellAudio.Play();
		}

		if (ClipboardCtrl != null)
		{
			ClipboardCtrl.ShowClipboards();
		}
	}
}
