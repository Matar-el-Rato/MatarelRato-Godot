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
	private Tween               _pulseTween;
	private bool _selectable = false;

	// ── Init ──────────────────────────────────────────────────────────────────

	/// <summary>Collision layer reserved for piece hover bodies — must match TableManager's mask.</summary>
	public const uint HoverLayer = 1u << 15;

	public void Initialize(string color, int pieceIndex)
	{
		PlayerColor = color;
		PieceIndex  = pieceIndex;
		CallDeferred(MethodName.ApplyColor);
		CallDeferred(MethodName.SetupHoverBody);
	}

	private void SetupHoverBody()
	{
		// A thin capsule that hugs the pawn shape, on a dedicated layer so the
		// TableManager ray can hit pieces directly without the tablero trimesh interfering.
		var body  = new StaticBody3D { Name = "HoverBody", CollisionLayer = HoverLayer, CollisionMask = 0 };
		var shape = new CollisionShape3D();
		shape.Shape    = new CapsuleShape3D { Radius = 0.025f, Height = 0.10f };
		shape.Position = new Vector3(0f, 0.05f, 0f);
		body.AddChild(shape);
		AddChild(body);
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

	// ── Hover highlight ───────────────────────────────────────────────────────

	/// <summary>
	/// Called by TableManager when the ray enters or exits this piece.
	/// Orange constant emission when on; restores the selectable pulse when off.
	/// </summary>
	public void SetHovered(bool on)
	{
		if (_borderMat == null) return;
		if (on)
		{
			_pulseTween?.Kill();
			_borderMat.EmissionEnabled          = true;
			_borderMat.Emission                 = new Color(1f, 0.50f, 0f);
			_borderMat.EmissionEnergyMultiplier = 1.8f;
		}
		else
		{
			if (_selectable)
				BeginPulse();
			else
			{
				_borderMat.EmissionEnabled          = false;
				_borderMat.EmissionEnergyMultiplier = 1.0f;
			}
		}
	}

	// ── Selectable highlight ──────────────────────────────────────────────────

	public void SetSelectable(bool on)
	{
		_selectable = on;
		if (_pulseTween != null) { _pulseTween.Kill(); _pulseTween = null; }
		if (_borderMat == null) return;
		if (on)
			BeginPulse();
		else
		{
			_borderMat.EmissionEnabled          = false;
			_borderMat.EmissionEnergyMultiplier = 1.0f;
		}
	}

	private void BeginPulse()
	{
		if (_borderMat == null) return;
		_pulseTween?.Kill();
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
