// ═══════════════════════════════════════════════════
// FichaNode.cs
// 3-D piece node for Parchís.
//
// Board index encoding (matches server + TableroController):
//   -1 / 0     → in base
//   1 … 68     → outer ring
//   101 … 108  → yellow corridor (108 = goal)
//   111 … 118  → blue   corridor (118 = goal)
//   121 … 128  → red    corridor (128 = goal)
//   131 … 138  → green  corridor (138 = goal)
// ═══════════════════════════════════════════════════
using Godot;

public partial class FichaNode : Node3D
{
	// ── Identity ──────────────────────────────────────────────────────────────
	public string PlayerColor { get; private set; }   // "yellow"|"green"|"red"|"blue"
	public int    PieceIndex  { get; private set; }   // 0-3 within the player

	// ── Board state ───────────────────────────────────────────────────────────
	public int BoardIndex { get; private set; } = -1;

	// ── Signals ───────────────────────────────────────────────────────────────
	[Signal] public delegate void PieceClickedEventHandler(FichaNode piece);

	// ── Private ───────────────────────────────────────────────────────────────
	private const string BorderSurface = "border";
	private StandardMaterial3D _borderMat;
	private ShaderMaterial      _hoverMat;
	private Tween               _pulseTween;
	private bool _selectable = false;
	private Area3D _clickArea;
	private readonly System.Collections.Generic.List<MeshInstance3D> _allMeshes = new();

	// ── Init ──────────────────────────────────────────────────────────────────

	public void Initialize(string color, int pieceIndex)
	{
		PlayerColor = color;
		PieceIndex  = pieceIndex;
		CallDeferred(MethodName.ApplyColor);
		CallDeferred(MethodName.SetupClickArea);
	}

	private void ApplyColor()
	{
		foreach (var node in FindChildren("*", "MeshInstance3D", true, false))
		{
			if (node is not MeshInstance3D mesh) continue;

			for (int i = 0; i < mesh.GetSurfaceOverrideMaterialCount(); i++)
			{
				string surfName = (mesh.Mesh as ArrayMesh)?.SurfaceGetName(i) ?? "";
				if (!surfName.ToLower().Contains(BorderSurface)) continue;

				var baseMat = mesh.Mesh.SurfaceGetMaterial(i) as StandardMaterial3D;
				if (baseMat == null) continue;

				var mat = (StandardMaterial3D)baseMat.Duplicate();
				mat.AlbedoColor = GetColorFor(PlayerColor);
				mesh.SetSurfaceOverrideMaterial(i, mat);
				_borderMat = mat;
				break; // one border surface is enough
			}
		}
	}

	private void SetupClickArea()
	{
		// Collect all mesh instances for hover overlay.
		CollectMeshes(this, _allMeshes);

		var shader = GD.Load<Shader>("res://Shaders/highlight.gdshader");
		if (shader != null)
		{
			_hoverMat = new ShaderMaterial { Shader = shader, RenderPriority = 10 };
			_hoverMat.SetShaderParameter("outline_color",          Colors.Yellow);
			_hoverMat.SetShaderParameter("thickness",              2.0f);
			_hoverMat.SetShaderParameter("smoothing_cutoff",       0.1f);
			_hoverMat.SetShaderParameter("smoothing_max",          0.1f);
			_hoverMat.SetShaderParameter("transparency_threshold", 0.1f);
			_hoverMat.SetShaderParameter("edge_sensitivity",       0.01f);
			_hoverMat.SetShaderParameter("occlusion_bias",         0.02f);
		}

		// Area3D is used only for click detection.
		// Hover is driven externally by TableManager.SetHovered() via ray casting,
		// because the tablero's trimesh StaticBody3D intercepts the physics pick ray
		// before it reaches this area.
		_clickArea = new Area3D { Name = "ClickArea", InputRayPickable = true };
		var shape = new CollisionShape3D();
		shape.Shape = new BoxShape3D { Size = new Vector3(0.08f, 0.14f, 0.08f) };
		var shapeOffset = new CollisionShape3D();
		_clickArea.AddChild(shape);
		shape.Position = new Vector3(0f, 0.06f, 0f);
		AddChild(_clickArea);

		_clickArea.InputEvent += (camera, ev, position, normal, shapeIndex) =>
		{
			if (!_selectable) return;
			if (FocusController.Instance == null || !FocusController.Instance.IsFocused) return;
			if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
				EmitSignal(SignalName.PieceClicked, this);
		};
	}

	/// <summary>
	/// Called by TableManager when the camera ray enters or exits this piece.
	/// Applies or removes the yellow outline shader overlay.
	/// </summary>
	public void SetHovered(bool on)
	{
		if (_hoverMat == null) return;
		foreach (var m in _allMeshes)
			m.MaterialOverlay = on ? _hoverMat : null;
	}

	private static void CollectMeshes(Node node, System.Collections.Generic.List<MeshInstance3D> list)
	{
		if (node is MeshInstance3D m) list.Add(m);
		foreach (Node child in node.GetChildren(true))
			CollectMeshes(child, list);
	}

	// ── Selectable highlight ──────────────────────────────────────────────────

	public void SetSelectable(bool on)
	{
		_selectable = on;
		if (!on) foreach (var m in _allMeshes) m.MaterialOverlay = null;

		if (_pulseTween != null) { _pulseTween.Kill(); _pulseTween = null; }

		if (_borderMat == null) return;
		if (on) {
			_borderMat.EmissionEnabled          = true;
			_borderMat.Emission                 = Colors.White;
			_borderMat.EmissionEnergyMultiplier = 0.0f;
			_pulseTween = CreateTween().SetLoops();
			_pulseTween.TweenMethod(
				Callable.From((float v) => _borderMat.EmissionEnergyMultiplier = v),
				0.0f, 0.4f, 0.7f)
				.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
			_pulseTween.TweenMethod(
				Callable.From((float v) => _borderMat.EmissionEnergyMultiplier = v),
				0.4f, 0.0f, 0.7f)
				.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		} else {
			_borderMat.EmissionEnabled          = false;
			_borderMat.EmissionEnergyMultiplier = 1.0f;
		}
	}

	// ── State helpers ─────────────────────────────────────────────────────────

	public void SetBoardIndex(int index) => BoardIndex = index;

	public bool IsInBase()   => BoardIndex <= 0;
	public bool IsFinished() => BoardIndex == 108 || BoardIndex == 118
							 || BoardIndex == 128 || BoardIndex == 138;

	// ── Static colour map ─────────────────────────────────────────────────────

	public static Color GetColorFor(string name) => name switch {
		"yellow" => new Color(1.00f, 0.85f, 0.10f),
		"green"  => new Color(0.10f, 0.72f, 0.18f),
		"red"    => new Color(0.90f, 0.12f, 0.12f),
		"blue"   => new Color(0.12f, 0.40f, 0.90f),
		_        => Colors.White,
	};
}
