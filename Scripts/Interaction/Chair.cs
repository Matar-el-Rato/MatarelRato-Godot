using Godot;
using System;

public partial class Chair : Node3D
{
    [Export] public Vector3 SitOffset = new Vector3(0, 0.45f, 0.15f);
    [Export] public float SitFOV = 100f;
    
    private Interactable _interactable;

    public override void _Ready()
    {
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
            _interactable.Interacted += OnInteracted;
            _interactable.UseLeftClick = true;
            _interactable.PromptText = "Sit";
            GD.Print($"[Chair] {Name} initialized with interactable {_interactable.Name}");
        }
        else
        {
            GD.PrintErr($"[Chair] {Name} could not find an Interactable child!");
        }
    }

    private void OnInteracted()
    {
        GD.Print($"[Chair] {Name} interacted! Searching for player...");
        var player = GetTree().GetFirstNodeInGroup("player") as PlayerCameraController;
        if (player == null)
        {
            // Fallback: try to find by type if group fails
            player = FindPlayerRecursive(GetTree().Root);
        }

        if (player != null)
        {
            player.Sit(this);
        }
    }

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
