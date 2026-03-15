// ═══════════════════════════════════════════════════
// BoardDemo.cs
// Visual demo: all 4 colours spawn at their home bases.
// Two of them (yellow & red) parade around the board
// one square at a time; the other two (blue & green)
// stay in their base corners as decoration.
//
// Attach as a child Node of the Tablero root node.
// Remove (or free) this node when real gameplay starts.
// ═══════════════════════════════════════════════════
using Godot;

public partial class BoardDemo : Node
{
	[Export] public float StepInterval = 0.4f;  // seconds per single-square hop

	// All four spawn so every base corner is visually populated.
	private static readonly string[] AllColors    = { "yellow", "blue", "red", "green" };
	// Only these two leave home and travel the board.
	private static readonly string[] MovingColors = { "yellow", "red" };

	private TableroController _board;
	private int _turn = 0;

	public override void _Ready()
	{
		_board = GetParent<TableroController>();
		// Defer everything: AddChild is blocked while the tree is setting up
		CallDeferred(MethodName.StartDemo);
	}

	// Called one frame after _Ready — safe to AddChild and read GlobalPosition
	private void StartDemo()
	{
		// Spawn all four so blue & green fill their base corners
		foreach (var color in AllColors)
			_board.SpawnPlayer(color);

		// Only exit the two that will travel
		foreach (var color in MovingColors)
			_board.ExitBase(color, 0);

		var timer = new Timer { WaitTime = StepInterval, Autostart = true };
		timer.Timeout += OnTick;
		AddChild(timer);
	}

	private void OnTick()
	{
		// Advance the active piece exactly one square so it visits every index
		string color = MovingColors[_turn % MovingColors.Length];
		_board.MovePiece(color, 0, 1);
		_turn++;
	}
}
