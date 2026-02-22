using Godot;
using System;
using System.Linq;
using System.Collections.Generic;
using ProyectoSoLib;

[Tool]
public partial class Board : Node2D
{
	private const int GridSize = 15;
	private int _cellSize = 32; // Slightly larger for better visibility
	private float _time = 0;
	
	// Modern Palette
	private Dictionary<string, Color> _palette = new Dictionary<string, Color>
	{
		{ "Red", new Color("#e74c3c") },
		{ "Blue", new Color("#3498db") },
		{ "Green", new Color("#2ecc71") },
		{ "Yellow", new Color("#f1c40f") },
		{ "White", new Color("#ecf0f1") },
		{ "Dark", new Color("#2c3e50") },
		{ "Shadow", new Color(0, 0, 0, 0.4f) }
	};

	// Dummy pieces for the engine demonstration
	private List<Ficha> _fichas = new List<Ficha>();

	public override void _Ready()
	{
		// Force nearest neighbor for the board
		TextureFilter = TextureFilterEnum.Nearest;
		
		// Initial piece setup (re-ensure they exist for editor)
		if (_fichas.Count == 0)
		{
			_fichas.Add(new Ficha("Red", "R1"));
			_fichas.Add(new Ficha("Blue", "B1"));
			_fichas.Add(new Ficha("Yellow", "Y1"));
			_fichas.Add(new Ficha("Green", "G1"));
		}

		if (!Engine.IsEditorHint())
		{
			CenterBoard();
		}

		// Apply stylized shader
		var material = new ShaderMaterial();
		material.Shader = GD.Load<Shader>("res://Shaders/Stylized.gdshader");
		this.Material = material;
	}

	public override void _Process(double delta)
	{
		_time += (float)delta;
		QueueRedraw(); // Ensure we animate every frame
	}

	private void CenterBoard()
	{
		Vector2 viewportSize = GetViewportRect().Size;
		// If in a smaller viewport (like SubViewport), center accordingly
		Position = (viewportSize / 2) - (new Vector2(GridSize, GridSize) * _cellSize / 2);
	}

	public override void _Draw()
	{
		// Draw Board Backdrop (Shadow)
		DrawRect(new Rect2(Vector2.One * 4, Vector2.One * (GridSize * _cellSize)), _palette["Shadow"]);
		
		// Draw Main Board Background
		DrawRect(new Rect2(Vector2.Zero, Vector2.One * (GridSize * _cellSize)), _palette["Dark"]);

		// 1. Draw Cells
		for (int r = 0; r < GridSize; r++)
		{
			for (int c = 0; c < GridSize; c++)
			{
				DrawCellAt(r, c);
			}
		}

		// 2. Draw Pulsing Center
		float pulse = (Mathf.Sin(_time * 2.0f) * 0.1f) + 0.9f;
		Rect2 centerRect = new Rect2(6 * _cellSize, 6 * _cellSize, 3 * _cellSize, 3 * _cellSize);
		DrawRect(centerRect.Grow(-2), _palette["White"].Lerp(_palette["Dark"], 0.1f * pulse));

		// 3. Draw Pieces (with float animation)
		DrawPieces();
	}

	private void DrawCellAt(int r, int c)
	{
		Rect2 rect = new Rect2(c * _cellSize, r * _cellSize, _cellSize, _cellSize).Grow(-1);
		
		bool isBase = (r < 6 && c < 6) || (r < 6 && c > 8) || (r > 8 && c < 6) || (r > 8 && c > 8);
		bool isPath = (r >= 6 && r <= 8) || (c >= 6 && c <= 8);

		if (isBase)
		{
			Color baseColor = _palette["Dark"];
			if (r < 6 && c < 6) baseColor = _palette["Yellow"];
			if (r < 6 && c > 8) baseColor = _palette["Blue"];
			if (r > 8 && c < 6) baseColor = _palette["Red"];
			if (r > 8 && c > 8) baseColor = _palette["Green"];
			DrawStylizedCell(rect, baseColor, true);
		}
		else if (isPath)
		{
			// Skip the center 3x3 as it's drawn separately
			if (r >= 6 && r <= 8 && c >= 6 && c <= 8) return;

			Color cellColor = _palette["White"];
			
			// Goal paths
			if (c == 7)
			{
				if (r > 0 && r < 7) cellColor = _palette["Blue"];
				if (r > 7 && r < 14) cellColor = _palette["Red"];
			}
			if (r == 7)
			{
				if (c > 0 && c < 7) cellColor = _palette["Yellow"];
				if (c > 7 && c < 14) cellColor = _palette["Green"];
			}

			DrawStylizedCell(rect, cellColor, false);
		}
	}

	private void DrawStylizedCell(Rect2 rect, Color color, bool isBase)
	{
		DrawRect(rect, color);
		
		// "Bevel" effect for pixel art look
		if (isBase)
		{
			DrawRect(new Rect2(rect.Position, new Vector2(rect.Size.X, 2)), new Color(1, 1, 1, 0.15f));
			DrawRect(new Rect2(new Vector2(rect.Position.X, rect.End.Y - 2), new Vector2(rect.Size.X, 2)), new Color(0, 0, 0, 0.15f));
		}
		else
		{
			DrawRect(new Rect2(rect.Position, new Vector2(rect.Size.X, 1)), new Color(1, 1, 1, 0.3f));
			DrawRect(new Rect2(new Vector2(rect.Position.X, rect.End.Y - 1), new Vector2(rect.Size.X, 1)), new Color(0, 0, 0, 0.2f));
		}
	}

	private void DrawPieces()
	{
		foreach (var ficha in _fichas)
		{
			// Simplistic mapping for demo: Use R/C from our WinForms engine logic if needed
			// Let's assume some dummy positions for the demo
			float floatY = Mathf.Sin(_time * 4.0f + ficha.GetId().GetHashCode()) * 4.0f;
			
			Vector2 gridPos = Vector2.Zero;
			if (ficha.GetColor() == "Red") gridPos = new Vector2(7, 10);
			else if (ficha.GetColor() == "Blue") gridPos = new Vector2(10, 7);
			else if (ficha.GetColor() == "Yellow") gridPos = new Vector2(4, 7);
			else if (ficha.GetColor() == "Green") gridPos = new Vector2(7, 4);

			Vector2 screenPos = gridPos * _cellSize + new Vector2(_cellSize / 2, _cellSize / 2);
			screenPos.Y += floatY;

			Color pColor = _palette[ficha.GetColor()];
			
			// Draw Shadow
			DrawCircle(screenPos + new Vector2(2, 4 - floatY), _cellSize * 0.3f, _palette["Shadow"]);
			
			// Draw Piece (Chip)
			DrawCircle(screenPos, _cellSize * 0.35f, pColor);
			DrawCircle(screenPos, _cellSize * 0.25f, pColor.Lightened(0.2f)); // Inner highlight
			DrawArc(screenPos, _cellSize * 0.35f, 0, Mathf.Pi * 2, 16, pColor.Darkened(0.3f), 2.0f); // Border
		}
	}
}
