// ═══════════════════════════════════════════════════
// TableroController.cs
// Owns all Parchís board state and piece movement.
//
// POSITION MARKERS (place Marker3D children in the editor):
//   Positions/Pos_01 … Pos_68          outer ring (1-indexed per rules)
//   HomePositions/HomeYellow_1 … _7    yellow home corridor (steps 1-7)
//   HomePositions/HomeGreen_1  … _7    green  home corridor
//   HomePositions/HomeRed_1    … _7    red    home corridor
//   HomePositions/HomeBlue_1   … _7    blue   home corridor
//   BasePositions/GoalYellow              yellow goal marker
//   BasePositions/GoalGreen               green  goal marker
//   BasePositions/GoalRed                 red    goal marker
//   BasePositions/GoalBlue                blue   goal marker
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
		_goalOccupancy.Remove(color); // reset goal state for this color on (re)spawn
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
	public async Task ApplyServerMove(string color, int pieceIndex, int from, int to, bool notifyCounter = false, int steps = 0)
	{
		var ficha = GetPiece(color, pieceIndex);
		if (ficha == null) return;

		RemoveFromOccupancy(ficha);
		ficha.SetBoardIndex(to);

		var path = BuildPath(color, from, to, steps);

		if (IsGoal(to))
		{
			// Register arrival order so we can assign a triangle slot.
			if (!_goalOccupancy.ContainsKey(color))
				_goalOccupancy[color] = new List<FichaNode>();
			var goalList = _goalOccupancy[color];
			if (!goalList.Contains(ficha))
				goalList.Add(ficha);

			var slotPos = GetGoalSlotPosition(color, goalList.IndexOf(ficha));

			if (notifyCounter && from <= 0)
			{
				await AnimatePath(ficha, path, color, false, slotPos);
				StepCounterByExit(steps);
			}
			else
				await AnimatePath(ficha, path, color, notifyCounter, slotPos);
			return;
		}

		if (notifyCounter && from <= 0)
		{
			await AnimatePath(ficha, path, color, false);
			StepCounterByExit(steps);
		}
		else
			await AnimatePath(ficha, path, color, notifyCounter);
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
			int    step = index % 10; // 1-7 = corridor, 8 = goal
			string cap  = Capitalize(color);
			marker = step == 8
				? GetNodeOrNull<Node3D>($"BasePositions/Goal{cap}")
				: GetNodeOrNull<Node3D>($"HomePositions/Home{cap}_{step}");
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

	// ── Gun misfire retreat ───────────────────────────────────────────────────

	/// <summary>
	/// Moves <paramref name="color"/>'s piece directly to <paramref name="toSq"/> with a
	/// short hop arc — used for the gun misfire step-back so we never use BuildPath
	/// (which would animate 67 steps forward around the ring).
	/// </summary>
	public async System.Threading.Tasks.Task ApplyPieceRetreat(string color, int pieceIndex, int toSq)
	{
		var ficha = GetPiece(color, pieceIndex);
		if (ficha == null) return;

		RemoveFromOccupancy(ficha);
		ficha.SetBoardIndex(toSq);
		AddToOccupancy(ficha, toSq);

		var dest = GetBoardWorldPosition(toSq, color);
		var mid  = (ficha.GlobalPosition + dest) * 0.5f + Vector3.Up * 0.08f;

		var tween = ficha.CreateTween().SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		tween.TweenProperty(ficha, "global_position", mid,  0.14f);
		tween.TweenProperty(ficha, "global_position", dest, 0.14f);
		await ToSignal(tween, Tween.SignalName.Finished);
	}

	// ── Fire axe: barrier discovery & visual highlights ───────────────────────

	// Pieces currently pulsing red for axe targeting (cleared on hide).
	private readonly List<FichaNode> _axeTargetedPieces = new();

	/// <summary>
	/// Returns the ring squares (1–68) that contain exactly two pieces forming an axeable
	/// barrier. Same-color barriers always count; mixed-color barriers count only on safe
	/// squares (where they form a real block). The previous and next ring squares must be
	/// empty — the axe splits the pair onto those, so we mirror the server's validation.
	/// </summary>
	public List<int> GetAxeableBarriers()
	{
		var result = new List<int>();
		foreach (var (sq, pieces) in _occupancy)
		{
			if (sq < 1 || sq > 68) continue;
			if (pieces == null || pieces.Count != 2) continue;

			bool sameColor = pieces[0].PlayerColor == pieces[1].PlayerColor;
			bool onSafe    = SafeSquares.Contains(sq);
			if (!sameColor && !onSafe) continue;

			int prev = sq == 1  ? 68 : sq - 1;
			int next = sq == 68 ? 1  : sq + 1;
			if (OccupantCount(prev) > 0 || OccupantCount(next) > 0) continue;

			result.Add(sq);
		}
		return result;
	}

	/// <summary>Makes the pieces at each barrier square pulse red to signal they're targetable.</summary>
	public void ShowAxeBarrierHighlights(List<int> squares)
	{
		HideAxeBarrierHighlights();
		foreach (int sq in squares)
		{
			foreach (var ficha in _pieces.Values)
			{
				if (ficha == null || ficha.BoardIndex != sq) continue;
				ficha.SetAxeTargeted(true);
				_axeTargetedPieces.Add(ficha);
			}
		}
	}

	public void HideAxeBarrierHighlights()
	{
		foreach (var ficha in _axeTargetedPieces)
			if (IsInstanceValid(ficha)) ficha.SetAxeTargeted(false);
		_axeTargetedPieces.Clear();
	}

	/// <summary>
	/// Applies a fire-axe split: arcs <paramref name="forwardPieceColor"/>'s piece to
	/// <paramref name="forwardTo"/> and <paramref name="backwardPieceColor"/>'s to
	/// <paramref name="backwardTo"/>. Awaitable — completes when both pieces have landed.
	/// </summary>
	public async Task ApplyFireAxeSplit(
		string forwardPieceColor,  int forwardPieceIndex,  int forwardTo,
		string backwardPieceColor, int backwardPieceIndex, int backwardTo)
	{
		var fwdFicha = GetPiece(forwardPieceColor,  forwardPieceIndex);
		var bwdFicha = GetPiece(backwardPieceColor, backwardPieceIndex);

		if (fwdFicha != null) { RemoveFromOccupancy(fwdFicha); fwdFicha.SetBoardIndex(forwardTo); }
		if (bwdFicha != null) { RemoveFromOccupancy(bwdFicha); bwdFicha.SetBoardIndex(backwardTo); }

		const float arcTime = 0.20f;
		Tween last = null;

		void Arc(FichaNode ficha, int destIndex, string color)
		{
			if (ficha == null) return;
			var dest = GetBoardWorldPosition(destIndex, color);
			var mid  = (ficha.GlobalPosition + dest) * 0.5f + Vector3.Up * 0.07f;
			var t = ficha.CreateTween()
				.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
			t.TweenProperty(ficha, "global_position", mid,  arcTime);
			t.TweenProperty(ficha, "global_position", dest, arcTime);
			last = t;
		}

		Arc(fwdFicha, forwardTo,  forwardPieceColor);
		Arc(bwdFicha, backwardTo, backwardPieceColor);

		if (fwdFicha != null) AddToOccupancy(fwdFicha, forwardTo);
		if (bwdFicha != null) AddToOccupancy(bwdFicha, backwardTo);

		if (last != null)
			await ToSignal(last, Tween.SignalName.Finished);
	}

	// ── Movement resolution ───────────────────────────────────────────────────

	/// <summary>
	/// Builds the full list of intermediate board indices the piece travels through.
	/// Used for visual animation only — not authoritative.
	/// Pass <paramref name="steps"/> (die total) to enable accurate bounce detection;
	/// without it, only corridor-backward bounces can be detected positionally.
	/// For bounce paths the goal square appears in the list followed by the return squares.
	/// </summary>
	public List<int> BuildPath(string color, int from, int to, int steps = 0)
	{
		var path = new List<int>();
		if (from <= 0) { path.Add(to); return path; } // exiting home — direct

		int entry    = HomeEntry[color];
		int corrBase = CorridorBase[color];
		int goalSq   = GoalSquare[color];

		// ── Bounce detection ──────────────────────────────────────────────────
		bool isBounce = false;
		if (steps > 0)
		{
			if (from >= 100)
			{
				int corrStep    = from - corrBase + 1;
				int stepsToGoal = 8 - corrStep;
				isBounce = steps > stepsToGoal;
			}
			else if (to >= 100) // ring → corridor, may overshoot
			{
				int stepsToEntry = entry >= from ? entry - from : (68 - from) + entry;
				int stepsToGoal  = stepsToEntry + 8;
				isBounce = steps > stepsToGoal;
			}
		}
		else if (from >= 100 && to >= corrBase && to <= from)
		{
			// No steps provided: positional fallback — to ≤ from in corridor must be a bounce.
			isBounce = true;
		}

		if (!isBounce)
		{
			// ── Normal forward path ──────────────────────────────────────────
			if (from >= 100) {
				for (int sq = from + 1; sq <= to; sq++)
					path.Add(sq);
				return path;
			}
			int pos = from;
			while (true) {
				if (pos == entry && to >= 100) {
					for (int corrSq = corrBase; corrSq <= to; corrSq++)
						path.Add(corrSq);
					return path;
				}
				pos = pos == 68 ? 1 : pos + 1;
				path.Add(pos);
				if (pos == to) return path;
				if (path.Count > 80) break;
			}
			return path;
		}

		// ── Bounce path: forward to goal, then backward to `to` ──────────────
		if (from >= 100)
		{
			for (int sq = from + 1; sq <= goalSq; sq++)
				path.Add(sq);
		}
		else
		{
			// Walk ring to entry, then full corridor to goal.
			int pos = from;
			while (pos != entry) {
				pos = pos == 68 ? 1 : pos + 1;
				path.Add(pos);
				if (path.Count > 80) break;
			}
			for (int corrSq = corrBase; corrSq <= goalSq; corrSq++)
				path.Add(corrSq);
		}
		// Backward from goal-1 to destination.
		for (int sq = goalSq - 1; sq >= to; sq--)
			path.Add(sq);

		return path;
	}

	// ── State mutation ────────────────────────────────────────────────────────

	// Steps the counter by the exit cost so residual dice keep the right count.
	// Falls back to DrainAll when steps is unknown (0).
	private static void StepCounterByExit(int steps)
	{
		if (steps > 0)
			for (int i = 0; i < steps; i++)
				MoveCounterHUD.Step();
		else
			MoveCounterHUD.DrainAll();
	}

	private const float HopHeight = 0.04f;
	private const float HopSpeed  = 0.08f;
	private const float LandSpeed = 0.10f;

	private async Task AnimatePath(FichaNode ficha, List<int> path, string color, bool notifyCounter = false, Vector3? finalDest = null)
	{
		if (path.Count == 0) return;

		var tween = CreateTween().SetTrans(Tween.TransitionType.Linear).SetEase(Tween.EaseType.InOut);

		int destIndex = path[^1];
		AddToOccupancy(ficha, destIndex); // no-op for goal squares (AddToOccupancy skips them)

		for (int i = 0; i < path.Count - 1; i++)
		{
			var wp = GetBoardWorldPosition(path[i], color);
			tween.TweenProperty(ficha, "global_position", wp + Vector3.Up * HopHeight, HopSpeed);
			if (notifyCounter)
				tween.TweenCallback(Callable.From(MoveCounterHUD.Step));
		}

		var dest = finalDest ?? GetBoardWorldPosition(destIndex, color);
		tween.TweenProperty(ficha, "global_position", dest + Vector3.Up * HopHeight, HopSpeed);
		if (notifyCounter)
			tween.TweenCallback(Callable.From(MoveCounterHUD.Step));
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
	/// For bounce paths (goal square mid-path), the return portion is offset sideways
	/// so the two legs of the path are visually distinct.
	/// Call <see cref="HidePathPreview"/> to remove it.
	/// </summary>
	public void ShowPathPreview(List<int> path, string color, Vector3? pieceWorldPos = null)
	{
		HidePathPreview();
		if (path.Count == 0) return;

		const float yOff     = 0.03f;
		const float totalDur = 0.9f;

		// ── Bounce detection: everything after the goal square shifts sideways ─
		int bounceIdx = -1;
		for (int i = 0; i < path.Count; i++)
			if (IsGoal(path[i])) { bounceIdx = i; break; }

		// Returns the world position for path[pathIdx], offset laterally on the return leg.
		Vector3 SquarePos(int sq, int pathIdx)
		{
			var p = GetBoardWorldPosition(sq, color);
			if (bounceIdx >= 0 && pathIdx > bounceIdx)
				p += GetLateralOffset(sq, color) * 2f;
			return p + Vector3.Up * yOff;
		}

		// Origin: piece's actual visual position so barrier-offset meshes start from the right place.
		Vector3 originPos = pieceWorldPos.HasValue
			? pieceWorldPos.Value + Vector3.Up * yOff
			: SquarePos(path[0], 0);

		// ── Connecting line (fade in over first 60 % of the animation) ────────
		var lineMat = new StandardMaterial3D
		{
			ShadingMode    = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor    = new Color(1f, 0.95f, 0.1f, 0f),  // start transparent
			Transparency   = BaseMaterial3D.TransparencyEnum.Alpha,
			NoDepthTest    = true,
			RenderPriority = 5,
		};

		// Line: origin → every path square; return-leg squares are laterally offset.
		// The very last vertex always uses the centered position so the line connects
		// cleanly to the destination ring (which is also placed at the centered square).
		{
			var lineMesh = new ImmediateMesh();
			lineMesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip, lineMat);
			lineMesh.SurfaceAddVertex(ToLocal(originPos));
			for (int i = 0; i < path.Count - 1; i++)
				lineMesh.SurfaceAddVertex(ToLocal(SquarePos(path[i], i)));
			// Last vertex: centered (no lateral offset), matching the destination ring.
			lineMesh.SurfaceAddVertex(ToLocal(GetBoardWorldPosition(path[^1], color) + Vector3.Up * yOff));
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

		// ── Intermediate dots: origin + all squares except destination ────────
		var dotMesh = new SphereMesh { Radius = 0.009f, Height = 0.018f, RadialSegments = 6, Rings = 3 };
		var dotMat  = new StandardMaterial3D
		{
			ShadingMode    = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor    = new Color(1f, 0.95f, 0.3f, 0.7f),
			Transparency   = BaseMaterial3D.TransparencyEnum.Alpha,
			NoDepthTest    = true,
			RenderPriority = 6,
		};

		// origin first, then all intermediate squares (path[0] … path[count-2])
		var dotPositions = new List<Vector3> { originPos };
		for (int i = 0; i < path.Count - 1; i++)
			dotPositions.Add(SquarePos(path[i], i));

		int dotCount = dotPositions.Count;
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
			dot.GlobalPosition = dotPositions[i];
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
		// Destination ring always at the centered square — that's where the piece lands.
		// The return-leg offset is visual-only for the intermediate dots/line, not the landing spot.
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

	// ── Goal triangle positioning ─────────────────────────────────────────────

	// Offset for pieces accumulating at the goal — 2× the barrier spread.
	private const float GoalPieceOffset = BarrierHalfOffset * 2f;

	// Key: color → pieces in arrival order (max 4).
	private readonly Dictionary<string, List<FichaNode>> _goalOccupancy = new();

	/// <summary>
	/// Returns the world position for the n-th piece (0-based) arriving at the goal.
	/// Derives the corridor forward direction from the last two corridor markers so the
	/// triangle always aligns with the actual corridor geometry.
	/// Slots: 0 = goal + forward, 1 = goal + perp, 2 = goal − perp, 3 = goal center.
	/// </summary>
	private Vector3 GetGoalSlotPosition(string color, int slotIndex)
	{
		string cap   = Capitalize(color);
		var goal8    = GetNodeOrNull<Node3D>($"BasePositions/Goal{cap}");
		var step7    = GetNodeOrNull<Node3D>($"HomePositions/Home{cap}_7");
		var step6    = GetNodeOrNull<Node3D>($"HomePositions/Home{cap}_6");

		Vector3 goalPos;
		Vector3 forward;

		if (goal8 != null && step7 != null)
		{
			var seg  = goal8.GlobalPosition - step7.GlobalPosition;
			var segH = new Vector3(seg.X, 0f, seg.Z);
			forward  = segH.LengthSquared() > 0.0001f ? segH.Normalized() : GetDefaultCorridorForward(color);
			goalPos  = goal8.GlobalPosition;
		}
		else if (step7 != null && step6 != null)
		{
			// Goal marker missing — extrapolate one step past marker 7.
			var seg  = step7.GlobalPosition - step6.GlobalPosition;
			var segH = new Vector3(seg.X, 0f, seg.Z);
			forward  = segH.LengthSquared() > 0.0001f ? segH.Normalized() : GetDefaultCorridorForward(color);
			goalPos  = step7.GlobalPosition + new Vector3(seg.X, 0f, seg.Z);
		}
		else
		{
			forward = GetDefaultCorridorForward(color);
			goalPos = goal8?.GlobalPosition ?? step7?.GlobalPosition ?? GlobalPosition;
			if (goalPos == GlobalPosition)
				GD.PushWarning($"[TableroController] No corridor markers found for {color} goal — pieces may appear at origin.");
		}

		// Perpendicular in the XZ plane (90° CCW rotation of forward).
		var perp = new Vector3(-forward.Z, 0f, forward.X);

		return slotIndex switch
		{
			0 => goalPos + forward * GoalPieceOffset,
			1 => goalPos + perp    * GoalPieceOffset,
			2 => goalPos - perp    * GoalPieceOffset,
			_ => goalPos,                              // slot 3 and 4th piece: center
		};
	}

	private static Vector3 GetDefaultCorridorForward(string color) =>
		(color == "yellow" || color == "red")
			? new Vector3(1f, 0f, 0f)
			: new Vector3(0f, 0f, 1f);

	// ── Barrier positioning ───────────────────────────────────────────────────

	private const float BarrierHalfOffset = 0.026f;

	// Returns the lateral offset vector for a board index.
	// Portrait squares (longitudinal axis = Z): pieces run along Z, so offset along Z.
	// Landscape squares (longitudinal axis = X): pieces run along X, so offset along X.
	private static Vector3 GetLateralOffset(int boardIndex, string color)
	{
		// Trapezoid squares (first of each corner pair) are flipped relative to their arm.
		if (boardIndex == 8  || boardIndex == 42) return new Vector3(0f, 0f, BarrierHalfOffset); // X→Z
		if (boardIndex == 25 || boardIndex == 59) return new Vector3(BarrierHalfOffset, 0f, 0f); // Z→X

		bool portrait;
		if (boardIndex >= 100) {
			// Corridor orientation follows its arm: yellow/red corridors are portrait, blue/green are landscape.
			portrait = color == "yellow" || color == "red";
		} else {
			portrait = (boardIndex >= 1  && boardIndex <= 7)  ||
					   (boardIndex >= 25 && boardIndex <= 41) ||
					   (boardIndex >= 59);
		}
		return portrait ? new Vector3(0f, 0f, BarrierHalfOffset)
						: new Vector3(BarrierHalfOffset, 0f, 0f);
	}

	/// <summary>
	/// Slides the two pieces at <paramref name="square"/> apart into a visible barrier.
	/// Works for same-color barriers and mixed-color barriers on safe squares.
	/// </summary>
	public void ApplyBarrierPositions(int square)
	{
		var atSquare = new List<FichaNode>();
		foreach (var ficha in _pieces.Values)
			if (ficha != null && ficha.BoardIndex == square)
				atSquare.Add(ficha);

		if (atSquare.Count < 2) return;

		string posColor = atSquare[0].PlayerColor;
		var center = GetBoardWorldPosition(square, posColor);
		var offset = GetLateralOffset(square, posColor);

		for (int i = 0; i < 2; i++)
		{
			var target = center + (i == 0 ? -offset : offset);
			var tw = CreateTween().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
			tw.TweenProperty(atSquare[i], "global_position", target, 0.25f);
		}
	}

	/// <summary>
	/// Slides the lone remaining piece at <paramref name="square"/> back to center after a barrier breaks.
	/// Works regardless of which color's piece remains.
	/// </summary>
	public void CenterPieceAt(int square)
	{
		foreach (var ficha in _pieces.Values)
		{
			if (ficha == null || ficha.BoardIndex != square) continue;
			var center = GetBoardWorldPosition(square, ficha.PlayerColor);
			var tw = CreateTween().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
			tw.TweenProperty(ficha, "global_position", center, 0.25f);
			break;
		}
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
