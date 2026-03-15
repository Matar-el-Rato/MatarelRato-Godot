// ═══════════════════════════════════════════════════
// TableroController.cs
// Owns all Parchís board state and piece movement.
//
// POSITION MARKERS (place Marker3D children in the editor):
//   Positions/Pos_01 … Pos_68          outer ring (1-indexed per rules)
//   HomePositions/HomeYellow_1 … _8    yellow home corridor
//   HomePositions/HomeGreen_1  … _8    green  home corridor
//   HomePositions/HomeRed_1    … _8    red    home corridor
//   HomePositions/HomeBlue_1   … _8    blue   home corridor
//   BasePositions/BaseYellow_0 … _3    yellow base slots
//   BasePositions/BaseGreen_0  … _3    green  base slots
//   BasePositions/BaseRed_0    … _3    red    base slots
//   BasePositions/BaseBlue_0   … _3    blue   base slots
//
// BOARD INDEX ENCODING (matches FichaNode):
//   -1         = in base
//   1 … 68     = outer ring
//   101 … 108  = home corridor steps 1-8
//   109        = finished
// ═══════════════════════════════════════════════════
using Godot;
using System.Collections.Generic;

public partial class TableroController : Node3D
{
	// ── Exports ───────────────────────────────────────────────────────────────
	[Export] public PackedScene FichaScene;

	// ── Board constants ───────────────────────────────────────────────────────

	// First square a piece lands on after rolling a 5 from base.
	public static readonly Dictionary<string, int> StartPositions = new() {
		{ "yellow",  5 },
		{ "blue",   22 },
		{ "red",    39 },
		{ "green",  56 },
	};

	// Safe squares where pieces cannot be eaten.
	public static readonly HashSet<int> SafeHouses = new() {
		5, 12, 17, 22, 29, 34, 39, 46, 51, 56, 63, 68
	};

	// The outer-ring square after which the piece enters its home corridor.
	// e.g. Yellow at ring pos 68 → next step enters HomeYellow_1.
	public static readonly Dictionary<string, int> HomeEntry = new() {
		{ "yellow", 68 },
		{ "green",  51 },
		{ "red",    34 },
		{ "blue",   17 },
	};

	// ── Runtime state ─────────────────────────────────────────────────────────

	// Key: "color_pieceIndex"  e.g. "red_2"
	private readonly Dictionary<string, FichaNode> _pieces = new();

	// Key: boardIndex → pieces occupying that square (max 2)
	private readonly Dictionary<int, List<FichaNode>> _occupancy = new();

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready() { }

	// ── Public API ────────────────────────────────────────────────────────────

	/// <summary>
	/// Spawns four pieces for <paramref name="color"/> and places them in their base.
	/// Call once per player at game start.
	/// </summary>
	public void SpawnPlayer(string color)
	{
		for (int i = 0; i < 4; i++)
		{
			var ficha = FichaScene.Instantiate<FichaNode>();
			AddChild(ficha);
			ficha.Initialize(color, i);
			ficha.SetBoardIndex(-1);
			ficha.GlobalPosition = GetBasePosition(color, i);
			_pieces[$"{color}_{i}"] = ficha;
		}
	}

	/// <summary>
	/// Moves a piece from base to its start square (requires rolling a 5).
	/// Returns false if the start square is barricaded by enemies.
	/// </summary>
	public bool ExitBase(string color, int pieceIndex)
	{
		var ficha = GetPiece(color, pieceIndex);
		if (ficha == null || !ficha.IsInBase()) return false;

		int target = StartPositions[color];
		if (!CanLand(ficha, target)) return false;

		ApplyMove(ficha, target);
		return true;
	}

	/// <summary>
	/// Advances a piece by <paramref name="steps"/>.
	/// Handles ring wrapping, home-corridor entry, eating, and barricade checks.
	/// Returns false if the move is illegal (overshoot, barricade, etc.).
	/// </summary>
	public bool MovePiece(string color, int pieceIndex, int steps)
	{
		var ficha = GetPiece(color, pieceIndex);
		if (ficha == null || ficha.IsInBase() || ficha.IsFinished()) return false;

		int target = ResolveTarget(ficha, steps, out bool valid);
		if (!valid) return false;
		if (!CanLand(ficha, target)) return false;

		// Check for path barricade (simplified: only final square checked).
		// A full implementation would step one square at a time.
		var eaten = FindEatable(ficha, target);
		ApplyMove(ficha, target);
		foreach (var e in eaten) SendToBase(e);
		return true;
	}

	// ── Movement resolution ───────────────────────────────────────────────────

	/// <summary>
	/// Computes the destination board index for <paramref name="steps"/> moves.
	/// Sets <paramref name="valid"/> = false if the move is impossible (overshoot corridor).
	/// </summary>
	private int ResolveTarget(FichaNode ficha, int steps, out bool valid)
	{
		valid = true;
		int current = ficha.BoardIndex;

		// Already in home corridor
		if (current >= 101)
		{
			int newStep = (current - 100) + steps;
			if (newStep > 8) { valid = false; return -1; }
			if (newStep == 8) return 109; // finished
			return 100 + newStep;
		}

		// On the outer ring — walk step by step
		int entry = HomeEntry[ficha.PlayerColor];
		int pos   = current;
		for (int i = 0; i < steps; i++)
		{
			if (pos == entry)
			{
				// Remaining steps enter the corridor
				int remaining = steps - i;
				if (remaining > 8) { valid = false; return -1; }
				if (remaining == 8) return 109;
				return 100 + remaining;
			}
			pos = pos == 68 ? 1 : pos + 1;
		}
		return pos;
	}

	// ── Landing rules ─────────────────────────────────────────────────────────

	private bool CanLand(FichaNode mover, int index)
	{
		if (index == 109) return true; // finishing square always ok
		if (!_occupancy.TryGetValue(index, out var occ) || occ.Count == 0) return true;
		if (occ.Count == 1) return true; // one occupant: share or eat
		// Two occupants = barricade; can only enter if both are yours
		// (a barricade of 2 of your own colour also blocks you → still false)
		return false;
	}

	/// <summary>Returns enemy pieces that get eaten when <paramref name="mover"/> lands on <paramref name="index"/>.</summary>
	private List<FichaNode> FindEatable(FichaNode mover, int index)
	{
		var result = new List<FichaNode>();
		// No eating on safe houses or inside home corridor
		if (SafeHouses.Contains(index) || index >= 100) return result;
		if (!_occupancy.TryGetValue(index, out var occ)) return result;

		foreach (var other in occ)
		{
			if (other.PlayerColor != mover.PlayerColor)
				result.Add(other);
		}
		return result;
	}

	// ── State mutation ────────────────────────────────────────────────────────

	private void ApplyMove(FichaNode ficha, int index)
	{
		// Remove from old position
		RemoveFromOccupancy(ficha);

		ficha.SetBoardIndex(index);

		if (index == 109) return; // finished — no position to track

		if (!_occupancy.ContainsKey(index))
			_occupancy[index] = new List<FichaNode>();
		_occupancy[index].Add(ficha);

		// Tween with a small hop arc
		var dest = GetBoardWorldPosition(index, ficha.PlayerColor);
		var tween = CreateTween().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
		tween.TweenProperty(ficha, "global_position", dest + Vector3.Up * 0.08f, 0.18f);
		tween.TweenProperty(ficha, "global_position", dest, 0.12f);
	}

	private void SendToBase(FichaNode ficha)
	{
		RemoveFromOccupancy(ficha);
		ficha.SetBoardIndex(-1);
		var dest  = GetBasePosition(ficha.PlayerColor, ficha.PieceIndex);
		var tween = CreateTween();
		tween.TweenProperty(ficha, "global_position", dest, 0.4f);
	}

	private void RemoveFromOccupancy(FichaNode ficha)
	{
		if (ficha.BoardIndex >= 0 && _occupancy.TryGetValue(ficha.BoardIndex, out var occ))
			occ.Remove(ficha);
	}

	// ── Position resolution ───────────────────────────────────────────────────

	/// <summary>
	/// Returns the world position for a given board index by looking up
	/// the matching Marker3D (or Node3D) child placed in the editor.
	/// </summary>
	public Vector3 GetBoardWorldPosition(int index, string color = "")
	{
		if (index <= 0) return GlobalPosition;

		Node3D marker;

		if (index >= 101 && index <= 108)
		{
			int    step = index - 100;
			string cap  = Capitalize(color);
			marker = GetNodeOrNull<Node3D>($"HomePositions/Home{cap}_{step}");
		}
		else
		{
			marker = GetNodeOrNull<Node3D>($"Positions/Pos_{index:D2}");
		}

		if (marker == null)
			GD.PushWarning($"[TableroController] Missing position marker for index {index} (color={color})");

		return marker?.GlobalPosition ?? GlobalPosition;
	}

	private Vector3 GetBasePosition(string color, int pieceIndex)
	{
		string cap    = Capitalize(color);
		var    marker = GetNodeOrNull<Node3D>($"BasePositions/Base{cap}_{pieceIndex}");
		return marker?.GlobalPosition ?? GlobalPosition + Vector3.Up * 0.05f;
	}

	// ── Queries ───────────────────────────────────────────────────────────────

	public FichaNode GetPiece(string color, int index)
	{
		_pieces.TryGetValue($"{color}_{index}", out var f);
		return f;
	}

	public bool IsSafeHouse(int index)  => SafeHouses.Contains(index);
	public bool IsBarricade(int index)  => _occupancy.TryGetValue(index, out var o) && o.Count >= 2;
	public int  OccupantCount(int index) => _occupancy.TryGetValue(index, out var o) ? o.Count : 0;

	// ── Utility ───────────────────────────────────────────────────────────────

	private static string Capitalize(string s) =>
		string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);
}
