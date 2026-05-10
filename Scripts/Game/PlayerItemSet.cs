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

    // Life casing nodes (shell, shell2, shell3 in the scene).
    private readonly List<Node3D> _casingNodes = new();
    private static readonly System.Collections.Generic.HashSet<string> _casingNames =
        new() { "shell", "shell2", "shell3" };

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void _Ready()
    {
        // Defer so instantiated child scenes finish their _Ready before we touch them.
        CallDeferred(MethodName.HideAndDisableAll);
        CallDeferred(MethodName.CollectCasings);
    }

    private void HideAndDisableAll()
    {
        foreach (Node child in GetChildren())
        {
            // Casings (shell nodes) stay visible — they always show lives.
            bool isCasing = _casingNames.Contains(child.Name.ToString());
            if (child is Node3D node3d)
            {
                _originalScales[child.Name] = node3d.Scale;
                if (!isCasing) node3d.Visible = false;
            }
            if (!isCasing) SetChildInteractions(child, false);
        }
    }

    private void CollectCasings()
    {
        foreach (Node child in GetChildren())
        {
            if (_casingNames.Contains(child.Name.ToString()) && child is Node3D n)
                _casingNodes.Add(n);
        }
        // Sort so shell < shell2 < shell3 = indices 0,1,2.
        _casingNodes.Sort((a, b) => string.Compare(a.Name, b.Name, System.StringComparison.Ordinal));
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

        // Disable physics bodies before scaling to near-zero — Jolt rejects singular transforms.
        SetStaticBodiesEnabled(item, false);
        item.Scale   = origScale * 0.001f;
        item.Visible = true;

        var tween = item.CreateTween();
        tween.TweenProperty(item, "scale", origScale, 0.45f)
             .SetTrans(Tween.TransitionType.Back)
             .SetEase(Tween.EaseType.Out);
        tween.TweenCallback(Callable.From(() => SetStaticBodiesEnabled(item, true)));

        if (_isOwned)
            SetChildInteractions(item, true);

        AddBurnFlash(item.GlobalPosition);
        AddEmbers(item.GlobalPosition);
    }

    /// <summary>
    /// Syncs casing visibility to <paramref name="lives"/> (0-3).
    /// Casings above the count tip over and disappear.
    /// </summary>
    public void SetLives(int lives)
    {
        lives = Mathf.Clamp(lives, 0, _casingNodes.Count);
        for (int i = 0; i < _casingNodes.Count; i++)
        {
            var casing = _casingNodes[i];
            if (!IsInstanceValid(casing)) continue;
            if (casing.Visible && i >= lives)
                DropCasing(casing);
            else if (!casing.Visible && i < lives)
                casing.Visible = true;
        }
    }

    private void DropCasing(Node3D casing)
    {
        var tween = casing.CreateTween();
        tween.SetParallel(true);
        tween.TweenProperty(casing, "rotation:z", Mathf.DegToRad(90f), 0.25f)
             .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        tween.TweenProperty(casing, "position:y", casing.Position.Y - 0.04f, 0.28f)
             .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        // Don't tween scale to zero — that's always a singular transform and Jolt complains.
        tween.Finished += () => { if (IsInstanceValid(casing)) casing.Visible = false; };
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

    private static void SetStaticBodiesEnabled(Node node, bool enabled)
    {
        if (node is StaticBody3D sb)
            sb.ProcessMode = enabled ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        foreach (Node child in node.GetChildren(true))
            SetStaticBodiesEnabled(child, enabled);
    }

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
