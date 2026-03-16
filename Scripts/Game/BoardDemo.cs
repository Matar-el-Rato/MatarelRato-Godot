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
		{
			_board.SpawnPlayer(color);
			_board.ExitBase(color, 0);
		}

		var timer = new Timer { WaitTime = StepInterval, Autostart = true };
		timer.Timeout += OnTick;
		AddChild(timer);
	}

	private void OnTick()
	{
		string color = Colors[_turn % Colors.Length];
		int steps = _rng.RandiRange(1, 5);

		// If the piece entered the home corridor or finished, loop it back out
		var piece = _board.GetPiece(color, 0);
		if (piece != null && (piece.BoardIndex >= 100 || piece.IsFinished()))
		{
			_board.ResetToStart(color, 0);
		}
		else
		{
			_board.MovePiece(color, 0, steps);
		}

		_turn++;
	}
}
