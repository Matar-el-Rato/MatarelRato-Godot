// ═══════════════════════════════════════════════════
// BellController.cs
// Drives the service bell prop: plays a bell sound and
// triggers the NPC's Appear() when the player interacts with it.
// ═══════════════════════════════════════════════════
using Godot;
using System;

/// <summary>
/// Attached to the service bell prop in the scene.
/// On interaction, plays the bell audio and calls <see cref="NPC.Appear()"/>
/// on the target NPC. The NPC can be set via <see cref="TargetNPCPath"/>
/// or auto-discovered as a sibling node.
/// </summary>
public partial class BellController : Node3D
{
	/// <summary>Global singleton — set in _Ready.</summary>
	public static BellController Instance { get; private set; }

	[Export] public AudioStreamPlayer3D BellAudio;
	[Export] public ClipboardController ClipboardCtrl;
	[Export] public NodePath            TargetNPCPath;
	[Export] public Interactable        InteractableNode;

	private NPC _targetNPC;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		Instance = this;

		InteractableNode ??= GetNodeOrNull<Interactable>("Interactable");
		BellAudio        ??= GetNodeOrNull<AudioStreamPlayer3D>("BellAudio");
		ClipboardCtrl    ??= GetParent()?.GetNodeOrNull<ClipboardController>("ClipboardController");

		// Resolve target NPC from the explicit path export first.
		if (TargetNPCPath != null && !TargetNPCPath.IsEmpty)
			_targetNPC = GetNodeOrNull<NPC>(TargetNPCPath);

		// Fallback: scan siblings for any NPC node.
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
			InteractableNode.Interacted += OnBellInteracted;
	}

	// ── Exclamation control ──────────────────────────────────────────────────

	/// <summary>Shows the bell's floating "!" indicator.</summary>
	public void ShowBellExclamation() => InteractableNode?.SetExclamationVisible(true);

	/// <summary>Hides the bell's floating "!" indicator.</summary>
	public void HideBellExclamation() => InteractableNode?.SetExclamationVisible(false);

	// ── Handler ───────────────────────────────────────────────────────────────

	private void OnBellInteracted()
	{
		BellAudio?.Play();
		HideBellExclamation();
		_targetNPC?.Appear();
	}
}
