// ═══════════════════════════════════════════════════
// TableroController.cs
// Owns all Parchís board state and piece movement.
//
// POSITION MARKERS (place Marker3D children in the editor):
//   Positions/Pos_01 … Pos_68          outer ring (1-indexed per rules)
//   HomePositions/HomeYellow_1 … _8    yellow home corridor (step 8 = goal)
//   HomePositions/HomeGreen_1  … _8    green  home corridor
//   HomePositions/HomeRed_1    … _8    red    home corridor
//   HomePositions/HomeBlue_1   … _8    blue   home corridor
//   BasePositions/BaseYellow_0 … _3    yellow base slots
//   BasePositions/BaseGreen_0  … _3    green  base slots
//   BasePositions/BaseRed_0    … _3    red    base slots
//   BasePositions/BaseBlue_0   … _3    blue   base slots
//
// BOARD INDEX ENCODING (matches server position encoding):
//   -1 / 0     = in base (client uses -1, server uses 0)
//   1 … 68     = outer ring
//   101 … 108  = yellow corridor HY1-HY7 + goal GY
//   111 … 118  = blue   corridor HB1-HB7 + goal GB
//   121 … 128  = red    corridor HR1-HR7 + goal GR
//   131 … 138  = green  corridor HG1-HG7 + goal GG
// ═══════════════════════════════════════════════════
using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

public partial class TableroController : Node3D
{
	// ── Exports ───────────────────────────────────────────────────────────────
	[Export] public PackedScene FichaScene;

	// ── Board constants ───────────────────────────────────────────────────────

	// Start square a piece lands on when exiting home.
	public static readonly Dictionary<string, int> StartPositions = new() {
		{ "yellow",  5 },
		{ "blue",   22 },
		{ "red",    39 },
		{ "green",  56 },
	};

	// Safe squares where pieces cannot be captured.
	public static readonly HashSet<int> SafeSquares = new() {
		1, 5, 12, 17, 22, 29, 34, 39, 46, 51, 56, 63, 68
	};

	// Last outer-ring square before the home corridor begins.
	public static readonly Dictionary<string, int> HomeEntry = new() {
		{ "yellow", 68 },
		{ "green",  51 },
		{ "red",    34 },
		{ "blue",   17 },
	};

	// First corridor square per color.
	public static readonly Dictionary<string, int> CorridorBase = new() {
		{ "yellow", 101 },
		{ "blue",   111 },
		{ "red",    121 },
		{ "green",  131 },
	};

	// Goal square per color (corridor step 8).
	public static readonly Dictionary<string, int> GoalSquare = new() {
		{ "yellow", 108 },
		{ "blue",   118 },
		{ "red",    128 },
		{ "green",  138 },
	};

	// ── Runtime state ─────────────────────────────────────────────────────────

	// Key: "color_pieceIndex"  e.g. "red_2"
	private readonly Dictionary<string, FichaNode> _pieces = new();

	// Key: boardIndex → pieces occupying that square
	private readonly Dictionary<int, List<FichaNode>> _occupancy = new();

	// Path preview nodes shown on hover.
	private readonly List<Node> _pathPreviewNodes = new();

	// Permanent golden square markers.
	private readonly List<Node> _goldenMarkers = new();

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready() { }

	// ── Public API ────────────────────────────────────────────────────────────

	/// <summary>Spawns four pieces for <paramref name="color"/> and places them in base, hidden until revealed.</summary>
	public void SpawnPlayer(string color)
	{
		for (int i = 0; i < 4; i++)
		{
			var ficha = FichaScene.Instantiate<FichaNode>();
			AddChild(ficha);
			ficha.Initialize(color, i);
			ficha.SetBoardIndex(-1);
			ficha.GlobalPosition = GetBasePosition(color, i);
			ficha.Visible        = false;
			_pieces[$"{color}_{i}"] = ficha;
		}
	}

	/// <summary>Scale-in reveals a piece that was hidden by SpawnPlayer.</summary>
	public void RevealFicha(string color, int pieceIndex)
	{
		var ficha = GetPiece(color, pieceIndex);
		if (ficha == null) return;

		// Disable all collision objects before scaling from near-zero to avoid Jolt singular-transform warnings.
		SetCollisionEnabled(ficha, false);

		ficha.Scale   = Vector3.One * 0.001f;
		ficha.Visible = true;

		var tween = ficha.CreateTween();
		tween.TweenProperty(ficha, "scale", Vector3.One, 0.35f)
			 .SetTrans(Tween.TransitionType.Back)
			 .SetEase(Tween.EaseType.Out);
		tween.TweenCallback(Callable.From(() =>
		{
			if (IsInstanceValid(ficha))
				SetCollisionEnabled(ficha, true);
		}));
	}

	/// <summary>
	/// Applies a server-authoritative move: moves piece from <paramref name="from"/> to
	/// <paramref name="to"/>.  Awaitable — completes when the piece animation finishes.
	/// </summary>
	public async Task ApplyServerMove(string color, int pieceIndex, int from, int to)
	{
		var ficha = GetPiece(color, pieceIndex);
		if (ficha == null) return;

		RemoveFromOccupancy(ficha);
		ficha.SetBoardIndex(to);

		if (IsGoal(to))
		{
			var dest  = GetBoardWorldPosition(to, color);
			var tween = CreateTween();
			tween.TweenProperty(ficha, "global_position", dest, 0.35f);
			await ToSignal(tween, Tween.SignalName.Finished);
			return;
		}

		var path = BuildPath(color, from, to);
		await AnimatePath(ficha, path, color);
	}

	/// <summary>Sends a piece back to its base slot (capture / triple-double).</summary>
	public void ReturnToBase(string color, int pieceIndex)
	{
		var ficha = GetPiece(color, pieceIndex);
		if (ficha == null) return;
		RemoveFromOccupancy(ficha);
		ficha.SetBoardIndex(-1);
		var dest  = GetBasePosition(color, pieceIndex);
		var tween = CreateTween();
		tween.TweenProperty(ficha, "global_position", dest, 0.4f);
	}

	/// <summary>Returns the 3D world position for a board index (ring, corridor, or base).</summary>
	public Vector3 GetBoardWorldPosition(int index, string color = "")
	{
		if (index <= 0) return GlobalPosition;

		Node3D marker;

		if (index >= 100) {
			// Corridor or goal: step = index % 10 (1-8 for HX1-Goal)
			int    step = index % 10;
			string cap  = Capitalize(color);
			marker = GetNodeOrNull<Node3D>($"HomePositions/Home{cap}_{step}");
		}
		else {
			marker = GetNodeOrNull<Node3D>($"Positions/Pos_{index:D2}");
		}

		if (marker == null)
			GD.PushWarning($"[TableroController] Missing position marker for index {index} (color={color})");

		return marker?.GlobalPosition ?? GlobalPosition;
	}

	// ── Queries ───────────────────────────────────────────────────────────────

	public FichaNode GetPiece(string color, int index)
	{
		_pieces.TryGetValue($"{color}_{index}", out var f);
		return f;
	}

	public bool IsSafeSquare(int index)  => SafeSquares.Contains(index);
	public bool IsBarricade(int index)   => _occupancy.TryGetValue(index, out var o) && o.Count >= 2;
	public int  OccupantCount(int index) => _occupancy.TryGetValue(index, out var o) ? o.Count : 0;

	public static bool IsGoal(int index) =>
		index == 108 || index == 118 || index == 128 || index == 138;

	// ── Movement resolution ───────────────────────────────────────────────────

	/// <summary>
	/// Builds the full list of intermediate board indices the piece travels through.
	/// Used for visual animation only — not authoritative.
	/// </summary>
	public List<int> BuildPath(string color, int from, int to)
	{
		var path = new List<int>();
		if (from <= 0) { path.Add(to); return path; } // exiting home — direct

		int entry    = HomeEntry[color];
		int corrBase = CorridorBase[color];

		if (from >= 100) {
			// Already in corridor
			for (int sq = from + 1; sq <= to; sq++)
				path.Add(sq);
			return path;
		}

		// Ring walk
		int pos = from;
		while (true) {
			if (pos == entry && to >= 100) {
				// Enter corridor
				for (int corrSq = corrBase; corrSq <= to; corrSq++)
					path.Add(corrSq);
				return path;
			}
			pos = pos == 68 ? 1 : pos + 1;
			path.Add(pos);
			if (pos == to) return path;
			if (path.Count > 80) break; // safety guard
		}
		return path;
	}

	// ── State mutation ────────────────────────────────────────────────────────

	private const float HopHeight = 0.04f;
	private const float HopSpeed  = 0.08f;
	private const float LandSpeed = 0.10f;

	private async Task AnimatePath(FichaNode ficha, List<int> path, string color)
	{
		if (path.Count == 0) return;

		var tween = CreateTween().SetTrans(Tween.TransitionType.Linear).SetEase(Tween.EaseType.InOut);

		int destIndex = path[^1];
		AddToOccupancy(ficha, destIndex);

		for (int i = 0; i < path.Count - 1; i++)
		{
			var wp = GetBoardWorldPosition(path[i], color);
			tween.TweenProperty(ficha, "global_position", wp + Vector3.Up * HopHeight, HopSpeed);
		}

		var dest = GetBoardWorldPosition(destIndex, color);
		tween.TweenProperty(ficha, "global_position", dest + Vector3.Up * HopHeight, HopSpeed);
		tween.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
		tween.TweenProperty(ficha, "global_position", dest, LandSpeed);

		await ToSignal(tween, Tween.SignalName.Finished);
	}

	private void RemoveFromOccupancy(FichaNode ficha)
	{
		if (ficha.BoardIndex >= 1 && _occupancy.TryGetValue(ficha.BoardIndex, out var occ))
			occ.Remove(ficha);
	}

	private void AddToOccupancy(FichaNode ficha, int index)
	{
		if (index <= 0 || IsGoal(index)) return;
		if (!_occupancy.ContainsKey(index))
			_occupancy[index] = new List<FichaNode>();
		_occupancy[index].Add(ficha);
	}

	// ── Position resolution ───────────────────────────────────────────────────

	private Vector3 GetBasePosition(string color, int pieceIndex)
	{
		string cap    = Capitalize(color);
		var    marker = GetNodeOrNull<Node3D>($"BasePositions/Base{cap}_{pieceIndex}");
		return marker?.GlobalPosition ?? GlobalPosition + Vector3.Up * 0.05f;
	}

	// ── Path preview ──────────────────────────────────────────────────────────

	/// <summary>
	/// Draws dots along <paramref name="path"/> and a pulsing ring at the destination,
	/// animating the reveal over ~1 second.
	/// Call <see cref="HidePathPreview"/> to remove it.
	/// </summary>
	public void ShowPathPreview(List<int> path, string color)
	{
		HidePathPreview();
		if (path.Count == 0) return;

		const float yOff     = 0.03f;
		const float totalDur = 0.9f;

		// ── Connecting line (fade in over first 60 % of the animation) ────────
		var lineMat = new StandardMaterial3D
		{
			ShadingMode    = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor    = new Color(1f, 0.95f, 0.1f, 0f),  // start transparent
			Transparency   = BaseMaterial3D.TransparencyEnum.Alpha,
			NoDepthTest    = true,
			RenderPriority = 5,
		};

		// LineStrip needs ≥ 2 vertices; skip when path is a single direct jump (base exit).
		if (path.Count >= 2)
		{
			var lineMesh = new ImmediateMesh();
			lineMesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip, lineMat);
			foreach (int sq in path)
				lineMesh.SurfaceAddVertex(ToLocal(GetBoardWorldPosition(sq, color) + Vector3.Up * yOff));
			lineMesh.SurfaceEnd();

			var lineInst = new MeshInstance3D { Mesh = lineMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
			AddChild(lineInst);
			_pathPreviewNodes.Add(lineInst);

			lineInst.CreateTween()
				.TweenMethod(
					Callable.From((float a) => lineMat.AlbedoColor = new Color(1f, 0.95f, 0.1f, a)),
					0f, 1f, totalDur * 0.6f)
				.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
		}

		// ── Intermediate dots (pop in staggered along path timing) ────────────
		var dotMesh = new SphereMesh { Radius = 0.009f, Height = 0.018f, RadialSegments = 6, Rings = 3 };
		var dotMat  = new StandardMaterial3D
		{
			ShadingMode    = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor    = new Color(1f, 0.95f, 0.3f, 0.7f),
			Transparency   = BaseMaterial3D.TransparencyEnum.Alpha,
			NoDepthTest    = true,
			RenderPriority = 6,
		};

		int dotCount = path.Count - 1;
		for (int i = 0; i < dotCount; i++)
		{
			var dot = new MeshInstance3D
			{
				Mesh             = dotMesh,
				MaterialOverride = dotMat,
				CastShadow       = GeometryInstance3D.ShadowCastingSetting.Off,
				Scale            = Vector3.Zero,
			};
			AddChild(dot);
			dot.GlobalPosition = GetBoardWorldPosition(path[i], color) + Vector3.Up * yOff;
			_pathPreviewNodes.Add(dot);

			float delay = dotCount > 1 ? (float)i / (dotCount - 1) * totalDur * 0.45f : 0f;
			var dt = dot.CreateTween();
			dt.TweenInterval(delay);
			dt.TweenProperty(dot, "scale", Vector3.One, 0.22f)
				.SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
		}

		// ── Destination ring (scale in last, then pulse) ──────────────────────
		// TorusMesh lies flat in the XZ plane by default — no rotation needed.
		int destSq  = path[^1];
		var destPos = GetBoardWorldPosition(destSq, color);

		var ringMat = new StandardMaterial3D
		{
			ShadingMode              = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor              = new Color(1f, 0.85f, 0f),
			EmissionEnabled          = true,
			Emission                 = new Color(1f, 0.7f, 0f),
			EmissionEnergyMultiplier = 0f,
			NoDepthTest              = true,
			RenderPriority           = 7,
		};
		var ring = new MeshInstance3D
		{
			Mesh             = new TorusMesh { InnerRadius = 0.016f, OuterRadius = 0.028f, Rings = 10, RingSegments = 10 },
			MaterialOverride = ringMat,
			CastShadow       = GeometryInstance3D.ShadowCastingSetting.Off,
			Scale            = Vector3.Zero,
		};
		AddChild(ring);
		ring.GlobalPosition = destPos;
		_pathPreviewNodes.Add(ring);

		// Scale in, then kick off the emission pulse.
		var ringSeq = ring.CreateTween();
		ringSeq.TweenInterval(totalDur * 0.55f);
		ringSeq.TweenProperty(ring, "scale", Vector3.One, totalDur * 0.35f)
			.SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
		ringSeq.TweenCallback(Callable.From(() =>
		{
			var pulse = ring.CreateTween().SetLoops();
			pulse.TweenMethod(
				Callable.From((float v) => ringMat.EmissionEnergyMultiplier = v),
				0.8f, 3.0f, 0.45f)
				.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
			pulse.TweenMethod(
				Callable.From((float v) => ringMat.EmissionEnergyMultiplier = v),
				3.0f, 0.8f, 0.45f)
				.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		}));
	}

	public void HidePathPreview()
	{
		foreach (var n in _pathPreviewNodes)
			if (IsInstanceValid(n)) n.QueueFree();
		_pathPreviewNodes.Clear();
	}

	/// <summary>Drops a permanent golden square marker on <paramref name="boardIndex"/> after a brimstone ray hit.</summary>
	public void MarkGoldenSquare(int boardIndex)
	{
		var pos = GetBoardWorldPosition(boardIndex);

		// Semi-transparent gold overlay — unshaded so lighting doesn't interfere.
		var mat = new StandardMaterial3D
		{
			ShadingMode              = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor              = new Color(1f, 0.70f, 0.08f,0.30f),
			Transparency             = BaseMaterial3D.TransparencyEnum.Alpha,
			EmissionEnabled          = true,
			Emission                 = new Color(1f, 0.72f, 0.1f),
			EmissionEnergyMultiplier = 12f,
			CullMode                 = BaseMaterial3D.CullModeEnum.Disabled,
			NoDepthTest              = false,
			RenderPriority           = 2,
		};

		// Rectangular tile — wider than deep to match parchis square proportions.
		// Rotated (portrait):     1-7 (top-right bar), 25-41 (left bar + bottom-left), 59+ (right bar).
		// Not rotated (landscape): 8-24 (top column + top-left), 42-58 (bottom + bottom-right).
		bool needsRotation = (boardIndex >= 1  && boardIndex <= 7)  ||
		                     (boardIndex >= 25 && boardIndex <= 41) ||
		                     (boardIndex >= 59);

		var marker = new MeshInstance3D
		{
			Mesh             = new BoxMesh { Size = new Vector3(0.128f, 0.0002f, 0.055f) },
			MaterialOverride = mat,
			CastShadow       = GeometryInstance3D.ShadowCastingSetting.Off,
			Scale            = Vector3.Zero,
		};
		AddChild(marker);
		marker.GlobalPosition  = pos;
		marker.RotationDegrees = needsRotation ? new Vector3(0f, 90f, 0f) : Vector3.Zero;
		_goldenMarkers.Add(marker);

		var scaleTween = marker.CreateTween();
		scaleTween.TweenProperty(marker, "scale", Vector3.One, 0.35f)
			.SetTrans(Tween.TransitionType.Back)
			.SetEase(Tween.EaseType.Out);
		scaleTween.TweenCallback(Callable.From(() =>
		{
			var pulse = marker.CreateTween().SetLoops();
			pulse.TweenMethod(
				Callable.From((float a) => mat.AlbedoColor = new Color(1f, 0.70f, 0.08f,a)),
				0.30f, 0.50f, 1.1f)
				.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
			pulse.TweenMethod(
				Callable.From((float a) => mat.AlbedoColor = new Color(1f, 0.70f, 0.08f,a)),
				0.50f, 0.30f, 1.1f)
				.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		}));
	}

	// ── Utility ───────────────────────────────────────────────────────────────

	private static string Capitalize(string s) =>
		string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);

	private static void SetCollisionEnabled(Node node, bool enabled)
	{
		if (node is CollisionObject3D col)
			col.ProcessMode = enabled ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
		foreach (Node child in node.GetChildren(true))
			SetCollisionEnabled(child, enabled);
	}
}
