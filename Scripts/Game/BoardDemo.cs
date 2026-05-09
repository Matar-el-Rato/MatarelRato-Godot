// ═══════════════════════════════════════════════════
// BoardDemo.cs
// Visual demo: all 4 colours spawn, exit base, and
// parade around the outer ring with random 1-5 rolls.
//
// Attach as a child Node of the Tablero root node.
// Remove (or free) this node when real gameplay starts.
// ═══════════════════════════════════════════════════
using Godot;

public partial class BoardDemo : Node
{
	[Export] public float StepInterval = 0.6f;

	private static readonly string[] Colors = { "yellow", "blue", "red", "green" };

	private TableroController _board;
	private RandomNumberGenerator _rng = new();
	private int _turn = 0;

	public override void _Ready()
	{
		_board = GetParent<TableroController>();
		CallDeferred(MethodName.StartDemo);
	}

	private void StartDemo()
	{
		foreach (var color in Colors)
			_board.SpawnPlayer(color);

		var timer = new Timer { WaitTime = StepInterval, Autostart = true };
		timer.Timeout += OnTick;
		AddChild(timer);
	}

	private void OnTick()
	{
		string color = Colors[_turn % Colors.Length];
		int    steps = _rng.RandiRange(1, 5);

		var piece = _board.GetPiece(color, 0);
		if (piece == null) { _turn++; return; }

		if (piece.IsInBase())
		{
			// Exit from base to start square.
			int startSq = TableroController.StartPositions[color];
			_board.ApplyServerMove(color, 0, -1, startSq);
		}
		else if (piece.IsFinished() || piece.BoardIndex >= 100)
		{
			// Loop back: send the piece home, it will re-exit next tick.
			_board.ReturnToBase(color, 0);
		}
		else
		{
			int from = piece.BoardIndex;
			int to   = ParchisLogic.Advance(color, from, steps);
			if (to < 0) to = TableroController.GoalSquare[color];
			_board.ApplyServerMove(color, 0, from, to);
		}

		_turn++;
	}
}
