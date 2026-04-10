// ═══════════════════════════════════════════════════
// CharacterSelector.cs
// ═══════════════════════════════════════════════════
using Godot;
using System;
using System.Collections.Generic;

public partial class CharacterSelector : Node3D
{
    // ── Exports ───────────────────────────────────────────────────────────────

    [Export] public NodePath PlayerPath = "../Player";
    [Export] public float ProximityRadius = 5.0f;
    [Export] public float EaseInDuration  = 1.1f;
    [Export] public float HiddenYOffset   = -3.5f;

    // ── Skin catalogue ────────────────────────────────────────────────────────

    private static readonly int[] ServerIds = { 101, 102, 103, 104, 105, 106 };

    // ── Private state ─────────────────────────────────────────────────────────

    private PlayerCameraController _playerController;
    private Selector               _selector;
    private Node3D                 _slotsParent;
    private AudioStreamPlayer      _burnAudio;

    // Single torus ring that slides between slots on selection
    private MeshInstance3D         _ring;
    private Tween                  _ringTween;
    private int                    _selectedIndex = 0;

    private List<MeshInstance3D>[] _slotMeshes;   // character meshes per slot for hover
    private ShaderMaterial         _hoverMaterial;

    private float _targetY;
    private bool  _isShown = false;
    private Tween _platformTween;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void _Ready()
    {
        _targetY = Position.Y;
        Position = new Vector3(Position.X, _targetY + HiddenYOffset, Position.Z);

        _playerController = GetNodeOrNull<PlayerCameraController>(PlayerPath);
        if (_playerController == null)
            _playerController = GetTree().Root.FindChild("Player", true, false)
                                    as PlayerCameraController;

        if (_playerController != null)
            _selector = _playerController.GetNodeOrNull<Selector>("Selector");

        _slotsParent = GetNodeOrNull<Node3D>("Slots");
        _slotMeshes  = new List<MeshInstance3D>[ServerIds.Length];

        BuildHoverMaterial();
        SetupAudio();
        WireSlots();

        // Defer mesh discovery and ring creation one frame so all GLB internals
        // have fully settled (their _Ready callbacks all run before this frame ends,
        // but some internal initialization is deferred inside the GLB scenes).
        CallDeferred(nameof(LateInit));
    }

    public override void _Process(double delta)
    {
        if (_playerController == null) return;

        var   p    = _playerController.GlobalPosition;
        var   m    = GlobalPosition;
        float dist = MathF.Sqrt((m.X - p.X) * (m.X - p.X) + (m.Z - p.Z) * (m.Z - p.Z));

        bool shouldShow = AuthManager.IsLoggedIn && dist < ProximityRadius;

        if (shouldShow  && !_isShown) EaseIn();
        if (!shouldShow &&  _isShown) EaseOut();
    }

    // ── Setup ─────────────────────────────────────────────────────────────────

    private void BuildHoverMaterial()
    {
        var shader = GD.Load<Shader>("res://Shaders/highlight.gdshader");
        if (shader == null) return;

        _hoverMaterial = new ShaderMaterial();
        _hoverMaterial.Shader         = shader;
        _hoverMaterial.RenderPriority = 10;
        _hoverMaterial.SetShaderParameter("outline_color",          new Color(1f, 0.85f, 0f));
        _hoverMaterial.SetShaderParameter("thickness",              2.0f);
        _hoverMaterial.SetShaderParameter("smoothing_cutoff",       0.1f);
        _hoverMaterial.SetShaderParameter("smoothing_max",          0.1f);
        _hoverMaterial.SetShaderParameter("transparency_threshold", 0.1f);
        _hoverMaterial.SetShaderParameter("edge_sensitivity",       0.01f);
        _hoverMaterial.SetShaderParameter("occlusion_bias",         0.02f);
    }

    private void SetupAudio()
    {
        var stream = GD.Load<AudioStream>("res://Assets/Sound FX/burnfx.wav");
        _burnAudio = new AudioStreamPlayer();
        _burnAudio.Stream   = stream;
        _burnAudio.VolumeDb = -3f;
        AddChild(_burnAudio);
    }

    private void WireSlots()
    {
        if (_slotsParent == null) return;

        int i = 0;
        foreach (Node child in _slotsParent.GetChildren())
        {
            if (i >= ServerIds.Length) break;
            if (child is not Node3D slot) { i++; continue; }

            int capturedIndex = i;

            // Hide the per-slot SelectionRing nodes — a single shared torus
            // ring is used instead and slides between slot positions.
            slot.GetNodeOrNull<MeshInstance3D>("SelectionRing")?.Hide();

            var interact = slot.GetNodeOrNull<Interactable>("Interactable");
            if (interact != null)
            {
                interact.PromptText            = "Change skin";
                interact.UseLeftClick          = true;
                interact.HandleHighlight       = false; // we apply MaterialOverlay manually
                interact.AutoGenerateCollision = false; // explicit HitBody capsule in tscn

                interact.Focused   += () => ApplyHover(capturedIndex, true);
                interact.Unfocused += () => ApplyHover(capturedIndex, false);
                interact.Interacted += () => OnSkinChosen(capturedIndex);
            }

            var orientFix = slot.GetNodeOrNull<Node3D>("Interactable/OrientationFix");
            if (orientFix != null && orientFix.GetChildCount() > 0)
                StartDisplayAnimation(orientFix.GetChild(0));

            i++;
        }
    }

    /// <summary>
    /// Runs deferred (next frame). Discovers GLB meshes for hover highlight and
    /// creates + positions the single shared selection ring.
    /// </summary>
    private void LateInit()
    {
        DiscoverMeshes();
        CreateRing();
    }

    private void DiscoverMeshes()
    {
        if (_slotsParent == null) return;
        int i = 0;
        foreach (Node child in _slotsParent.GetChildren())
        {
            if (i >= ServerIds.Length) break;
            if (child is not Node3D slot) { i++; continue; }
            _slotMeshes[i] = new List<MeshInstance3D>();
            var orientFix = slot.GetNodeOrNull<Node3D>("Interactable/OrientationFix");
            if (orientFix != null) CollectMeshes(orientFix, _slotMeshes[i]);
            i++;
        }
    }

    private static void CollectMeshes(Node node, List<MeshInstance3D> result)
    {
        if (node is MeshInstance3D mi) result.Add(mi);
        foreach (Node c in node.GetChildren(true))
            CollectMeshes(c, result);
    }

    // ── Shared selection ring ─────────────────────────────────────────────────

    private void CreateRing()
    {
        var torus = new TorusMesh();
        torus.InnerRadius    = 0.28f;  // small tight ring
        torus.OuterRadius    = 0.32f;  // tube diameter ≈ 0.04 — very thin
        torus.Rings          = 48;
        torus.RingSegments   = 10;

        var mat = new StandardMaterial3D();
        mat.ShadingMode              = BaseMaterial3D.ShadingModeEnum.Unshaded;
        mat.AlbedoColor              = new Color(0.85f, 0.93f, 1f);
        mat.EmissionEnabled          = true;
        mat.Emission                 = new Color(0.25f, 0.55f, 1.00f);
        mat.EmissionEnergyMultiplier = 6.0f;

        _ring = new MeshInstance3D();
        _ring.Name             = "SelectionRing";
        _ring.Mesh             = torus;
        _ring.MaterialOverride = mat;
        _ring.CastShadow       = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(_ring);

        // OmniLight so the ring actually casts blue light on the platform surface
        var light = new OmniLight3D();
        light.LightColor    = new Color(0.3f, 0.6f, 1.0f);
        light.LightEnergy   = 2.0f;
        light.OmniRange     = 1.5f;
        light.ShadowEnabled = false;
        _ring.AddChild(light);

        // Snap to initial slot without tweening
        _ring.Position = SlotLocalPos(_selectedIndex);
    }

    /// <summary>
    /// Returns the ring's target position in CharacterSelector local space for
    /// the given slot index — just above the platform surface (y=0.17).
    /// X/Z are taken from the slot's position relative to this node.
    /// </summary>
    private Vector3 SlotLocalPos(int slotIndex)
    {
        if (_slotsParent == null || slotIndex >= _slotsParent.GetChildCount())
            return Vector3.Zero;

        var slot = _slotsParent.GetChild(slotIndex) as Node3D;
        if (slot == null) return Vector3.Zero;

        // slot.Position is relative to _slotsParent.
        // _slotsParent.Position is relative to this (CharacterSelector).
        var posInMySpace = _slotsParent.Position + slot.Position;
        return new Vector3(posInMySpace.X, 0.17f, posInMySpace.Z);
    }

    // ── Hover highlight ───────────────────────────────────────────────────────

    private void ApplyHover(int index, bool active)
    {
        if (_hoverMaterial == null) return;
        if (_slotMeshes == null || index >= _slotMeshes.Length || _slotMeshes[index] == null)
            return;

        foreach (var mesh in _slotMeshes[index])
            if (IsInstanceValid(mesh))
                mesh.MaterialOverlay = active ? _hoverMaterial : null;
    }

    // ── Skin selection ────────────────────────────────────────────────────────

    /// <summary>
    /// Snaps the selection ring to the slot matching <paramref name="skinId"/>
    /// without tweening. Called after login to reflect the restored skin.
    /// </summary>
    public void SnapToSkin(int skinId)
    {
        for (int i = 0; i < ServerIds.Length; i++)
        {
            if (ServerIds[i] != skinId) continue;
            _selectedIndex = i;
            if (_ring != null)
                _ring.Position = SlotLocalPos(i);
            return;
        }
    }

    private void OnSkinChosen(int index)
    {
        if (_playerController == null) return;

        _selectedIndex = index;

        // Slide the ring to the new slot with a smooth ease
        if (_ring != null)
        {
            _ringTween?.Kill();
            _ringTween = CreateTween();
            _ringTween
                .TweenProperty(_ring, "position", SlotLocalPos(index), 0.4f)
                .SetTrans(Tween.TransitionType.Sine)
                .SetEase(Tween.EaseType.InOut);
        }


        _burnAudio?.Play();

        int targetServerId = ServerIds[index];
        if (_selector?.Entries != null)
        {
            foreach (var entry in _selector.Entries)
            {
                if (entry == null || entry.ServerId != targetServerId) continue;
                _playerController.SwapCharacter(entry);

                // Persist skin on the server (fire-and-forget background thread)
                int uid = AuthManager.UserId;
                int sid = targetServerId;
                System.Threading.Tasks.Task.Run(() =>
                {
                    var r = ServerProtocol.ChangeSkin(
                        ServerProtocol.DefaultHost, ServerProtocol.DefaultPort, uid, sid);
                    if (!r.IsSuccess)
                        GD.PrintErr($"[CharacterSelector] REQ_CHANGE_SKIN failed: {r.Message}");
                });
                return;
            }
        }

        GD.PrintErr($"[CharacterSelector] No CharacterEntry for ServerId {targetServerId}.");
    }

    // ── Ease in / out ─────────────────────────────────────────────────────────

    private void EaseIn()
    {
        _isShown = true;
        _platformTween?.Kill();
        _platformTween = CreateTween();
        _platformTween
            .TweenProperty(this, "position:y", _targetY, EaseInDuration)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.Out);

        SpawnEmbers(GlobalPosition + Vector3.Up * 0.15f, 80, 1.2f);
        _burnAudio?.Play();
    }

    private void EaseOut()
    {
        _isShown = false;
        _platformTween?.Kill();
        _platformTween = CreateTween();
        _platformTween
            .TweenProperty(this, "position:y", _targetY + HiddenYOffset, EaseInDuration * 0.55f)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.In);

        SpawnEmbers(GlobalPosition + Vector3.Up * 0.15f, 60, 1.0f);
        _burnAudio?.Play();
    }

    // ── Fire VFX ─────────────────────────────────────────────────────────────

    private void SpawnEmbers(Vector3 worldPos, int amount, float lifetime)
    {
        var p = new CpuParticles3D();
        p.TopLevel = true;
        GetParent().AddChild(p);
        p.GlobalPosition      = worldPos;
        p.Amount              = amount;
        p.Lifetime            = lifetime;
        p.OneShot             = true;
        p.Explosiveness       = 0.85f;
        p.EmissionShape       = CpuParticles3D.EmissionShapeEnum.Sphere;
        p.EmissionSphereRadius = 0.2f;
        p.Direction           = new Vector3(0, 1, 0);
        p.Spread              = 55f;
        p.Gravity             = new Vector3(0, 2f, 0);
        p.InitialVelocityMin  = 1.0f;
        p.InitialVelocityMax  = 2.5f;
        p.ScaleAmountMin      = 0.8f;
        p.ScaleAmountMax      = 1.5f;

        var g = new Gradient();
        g.SetColor(0, new Color(1f, 1f, 0.5f, 1f));
        g.AddPoint(0.3f, new Color(1f, 0.5f, 0.1f, 0.9f));
        g.SetColor(g.GetPointCount() - 1, new Color(0.8f, 0.1f, 0f, 0f));
        p.ColorRamp = g;

        var qm = new QuadMesh(); qm.Size = new Vector2(0.018f, 0.018f);
        p.Mesh = qm;

        var mat = new StandardMaterial3D();
        mat.ShadingMode            = StandardMaterial3D.ShadingModeEnum.Unshaded;
        mat.VertexColorUseAsAlbedo = true;
        mat.BillboardMode          = StandardMaterial3D.BillboardModeEnum.Enabled;
        mat.Transparency           = StandardMaterial3D.TransparencyEnum.Alpha;
        p.MaterialOverride = mat;

        p.Emitting = true;
        GetTree().CreateTimer(lifetime + 0.5f).Timeout += () => { if (IsInstanceValid(p)) p.QueueFree(); };
    }

    private void SpawnSmoke(Vector3 worldPos, int amount, float lifetime)
    {
        var s = new CpuParticles3D();
        s.TopLevel = true;
        GetParent().AddChild(s);
        s.GlobalPosition      = worldPos;
        s.Amount              = amount;
        s.Lifetime            = lifetime;
        s.OneShot             = true;
        s.Explosiveness       = 0.5f;
        s.EmissionShape       = CpuParticles3D.EmissionShapeEnum.Sphere;
        s.EmissionSphereRadius = 0.12f;
        s.Direction           = new Vector3(0, 1, 0);
        s.Spread              = 35f;
        s.Gravity             = new Vector3(0, 0.5f, 0);
        s.InitialVelocityMin  = 0.5f;
        s.InitialVelocityMax  = 0.8f;
        s.ScaleAmountMin      = 1.0f;
        s.ScaleAmountMax      = 3.0f;

        var g = new Gradient();
        g.SetColor(0, new Color(0.15f, 0.1f, 0.08f, 0.4f));
        g.SetColor(1, new Color(0.1f, 0.08f, 0.06f, 0f));
        s.ColorRamp = g;

        var sq = new QuadMesh(); sq.Size = new Vector2(0.06f, 0.06f);
        s.Mesh = sq;

        var mat = new StandardMaterial3D();
        mat.ShadingMode            = StandardMaterial3D.ShadingModeEnum.Unshaded;
        mat.VertexColorUseAsAlbedo = true;
        mat.BillboardMode          = StandardMaterial3D.BillboardModeEnum.Enabled;
        mat.Transparency           = StandardMaterial3D.TransparencyEnum.Alpha;
        s.MaterialOverride = mat;

        s.Emitting = true;
        GetTree().CreateTimer(lifetime + 1.0f).Timeout += () => { if (IsInstanceValid(s)) s.QueueFree(); };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void StartDisplayAnimation(Node model)
    {
        var ap = model.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
        if (ap != null && ap.HasAnimation("WalkingCycle_001"))
            ap.Play("WalkingCycle_001");
    }
}
