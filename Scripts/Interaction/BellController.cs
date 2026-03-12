using Godot;
using System;

public partial class BellController : Node3D
{
	[Export] public AudioStreamPlayer3D BellAudio;
	[Export] public ClipboardController ClipboardCtrl;
	[Export] public NodePath TargetNPCPath;
	[Export] public Interactable InteractableNode;

	private NPC _targetNPC;

	public override void _Ready()
	{
		InteractableNode ??= GetNodeOrNull<Interactable>("Interactable");
		BellAudio ??= GetNodeOrNull<AudioStreamPlayer3D>("BellAudio");
		ClipboardCtrl ??= GetParent()?.GetNodeOrNull<ClipboardController>("ClipboardController");

		// Resolve NPC from path
		if (TargetNPCPath != null && !TargetNPCPath.IsEmpty)
		{
			_targetNPC = GetNodeOrNull<NPC>(TargetNPCPath);
		}

		// Fallback: search sibling nodes for any NPC
		if (_targetNPC == null && GetParent() != null)
		{
			foreach (var child in GetParent().GetChildren())
			{
				if (child is NPC npc)
				{
					_targetNPC = npc;
					break;
				}
			}
		}

		if (InteractableNode != null)
		{
			InteractableNode.Interacted += OnBellInteracted;
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

		if (_targetNPC != null)
		{
			_targetNPC.Appear();
		}
	}
}
