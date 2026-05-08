using Godot;
using System.Collections.Generic;

/// <summary>
/// Manages per-player item visibility and ownership.
/// All items start fully hidden at game start; Ophanim reveals them one by one via SpawnItem.
/// Interaction is only enabled for items that belong to the local player's set AND have been spawned.
/// </summary>
public partial class PlayerItemSet : Node3D
{
    [Export] public int SlotIndex = -1;

    private bool _isOwned = false;

    // Original scale saved per direct child name so we can restore after the spawn scale-in.
    private readonly Dictionary<string, Vector3> _originalScales = new();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void _Ready()
    {
        // Defer so instantiated child scenes finish their _Ready before we touch them.
        CallDeferred(MethodName.HideAndDisableAll);
    }

    private void HideAndDisableAll()
    {
        foreach (Node child in GetChildren())
        {
            if (child is Node3D node3d)
            {
                _originalScales[child.Name] = node3d.Scale;
                node3d.Visible = false;
            }
            SetChildInteractions(child, false);
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Enables or disables all interactions. True when the local player owns this set.</summary>
    public void SetOwned(bool owned)
    {
        _isOwned = owned;
        // Only enable interaction on items that are already visible (spawned).
        foreach (Node child in GetChildren())
        {
            if (child is Node3D { Visible: true })
                SetChildInteractions(child, owned);
        }
    }

    /// <summary>Returns the world position of the named item node (valid even while hidden).</summary>
    public Vector3 GetItemWorldPosition(string itemName)
    {
        string nodeName = ItemNameToNodeName(itemName);
        return GetNodeOrNull<Node3D>(nodeName)?.GlobalPosition ?? GlobalPosition;
    }

    /// <summary>
    /// Makes an item visible with a scale-in tween and fire VFX.
    /// If the set is owned by the local player, also enables interaction.
    /// </summary>
    public void SpawnItem(string itemName)
    {
        string nodeName = ItemNameToNodeName(itemName);
        var item = GetNodeOrNull<Node3D>(nodeName);
        if (item == null)
        {
            GD.PrintErr($"[PIS] SpawnItem: '{nodeName}' not found in {Name}");
            return;
        }

        Vector3 origScale = _originalScales.TryGetValue(nodeName, out var s) ? s : item.Scale;
        item.Scale   = origScale * 0.001f;
        item.Visible = true;

        var tween = item.CreateTween();
        tween.TweenProperty(item, "scale", origScale, 0.45f)
             .SetTrans(Tween.TransitionType.Back)
             .SetEase(Tween.EaseType.Out);

        if (_isOwned)
            SetChildInteractions(item, true);

        AddBurnFlash(item.GlobalPosition);
        AddEmbers(item.GlobalPosition);
    }

    // ── VFX helpers ───────────────────────────────────────────────────────────

    private void AddBurnFlash(Vector3 worldPos)
    {
        var flash = new OmniLight3D
        {
            TopLevel    = true,
            LightColor  = new Color(1.0f, 0.5f, 0.2f),
            LightEnergy = 0.0f,
            OmniRange   = 4.0f,
        };
        AddChild(flash);
        flash.GlobalPosition = worldPos + Vector3.Up * 0.2f;
        var t = CreateTween();
        t.TweenProperty(flash, "light_energy", 4.0f, 0.07f);
        t.TweenProperty(flash, "light_energy", 0.0f, 0.38f);
        t.Finished += () => flash.QueueFree();
    }

    private void AddEmbers(Vector3 worldPos)
    {
        var particles = new CpuParticles3D { TopLevel = true };
        AddChild(particles);
        particles.GlobalPosition     = worldPos + Vector3.Up * 0.3f;
        particles.Amount             = 80;
        particles.Lifetime           = 0.7f;
        particles.OneShot            = true;
        particles.Explosiveness      = 0.85f;
        particles.EmissionShape      = CpuParticles3D.EmissionShapeEnum.Box;
        particles.EmissionBoxExtents = new Vector3(0.25f, 0.4f, 0.25f);
        particles.Direction          = new Vector3(0, 1, 0);
        particles.Spread             = 50.0f;
        particles.Gravity            = new Vector3(0, 2.0f, 0);
        particles.InitialVelocityMin = 0.8f;
        particles.InitialVelocityMax = 2.0f;
        particles.ScaleAmountMin     = 0.6f;
        particles.ScaleAmountMax     = 1.2f;

        var gradient = new Gradient();
        gradient.SetColor(0, new Color(1, 1, 0.5f, 1));
        gradient.AddPoint(0.3f, new Color(1, 0.5f, 0.1f, 0.9f));
        gradient.SetColor(gradient.GetPointCount() - 1, new Color(0.8f, 0.1f, 0, 0));
        particles.ColorRamp = gradient;
        particles.Mesh = new QuadMesh { Size = new Vector2(0.016f, 0.016f) };
        particles.MaterialOverride = new StandardMaterial3D
        {
            ShadingMode            = StandardMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            BillboardMode          = StandardMaterial3D.BillboardModeEnum.Enabled,
            Transparency           = StandardMaterial3D.TransparencyEnum.Alpha,
        };
        particles.Emitting = true;
        GetTree().CreateTimer(particles.Lifetime + 0.5f).Timeout +=
            () => { if (IsInstanceValid(particles)) particles.QueueFree(); };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ItemNameToNodeName(string itemName) => itemName switch
    {
        "gun"              => "Gun",
        "cigarette"        => "CigaretteInteraction",
        "magnifying_glass" => "MagnifyingGlass",
        "handcuffs"        => "Handcuffs",
        "fire_axe"         => "FireAxe",
        _                  => itemName,
    };

    private void SetChildInteractions(Node node, bool enabled)
    {
        if (node is CollisionObject3D col)
            col.InputRayPickable = enabled;

        if (node is Interactable interactable)
            interactable.Enabled = enabled;

        foreach (Node child in node.GetChildren())
            SetChildInteractions(child, enabled);
    }
}
