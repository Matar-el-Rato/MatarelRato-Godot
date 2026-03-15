// ═══════════════════════════════════════════════════
// FichaNode.cs
// 3-D piece node for Parchís.
// Colour is applied by overriding the "border" surface
// material on the imported ficha.glb mesh.
//
// Board index encoding:
//   -1          → in base (not yet on board)
//   1 … 68      → outer ring (1-indexed, matching rule positions)
//   101 … 108   → home corridor steps 1-8
//   109         → finished (safe home)
// ═══════════════════════════════════════════════════
using Godot;

public partial class FichaNode : Node3D
{
	// ── Identity ──────────────────────────────────────────────────────────────
	public string PlayerColor { get; private set; }   // "yellow" | "green" | "red" | "blue"
	public int    PieceIndex  { get; private set; }   // 0-3 within the player

	// ── Board state ───────────────────────────────────────────────────────────
	public int BoardIndex { get; private set; } = -1;

	// Name of the surface/material inside ficha.glb that gets tinted.
	private const string BorderSurface = "border";

	// ── Init ──────────────────────────────────────────────────────────────────

	/// <summary>Called by TableroController right after instancing.</summary>
	public void Initialize(string color, int pieceIndex)
	{
		PlayerColor = color;
		PieceIndex  = pieceIndex;
		CallDeferred(MethodName.ApplyColor);
	}

	private void ApplyColor()
	{
		// Walk all MeshInstance3D descendants and tint any surface named "border".
		foreach (var node in FindChildren("*", "MeshInstance3D", true, false))
		{
			if (node is not MeshInstance3D mesh) continue;

			for (int i = 0; i < mesh.GetSurfaceOverrideMaterialCount(); i++)
			{
				string surfName = (mesh.Mesh as ArrayMesh)?.SurfaceGetName(i) ?? "";
				if (!surfName.ToLower().Contains(BorderSurface)) continue;

				// Duplicate so each piece gets its own material instance.
				var baseMat = mesh.Mesh.SurfaceGetMaterial(i) as StandardMaterial3D;
				if (baseMat == null) continue;

				var mat = (StandardMaterial3D)baseMat.Duplicate();
				mat.AlbedoColor = GetColorFor(PlayerColor);
				mesh.SetSurfaceOverrideMaterial(i, mat);
			}
		}
	}

	// ── State helpers ─────────────────────────────────────────────────────────

	public void SetBoardIndex(int index) => BoardIndex = index;

	public bool IsInBase()   => BoardIndex == -1;
	public bool IsFinished() => BoardIndex == 109;

	// ── Static colour map ─────────────────────────────────────────────────────

	public static Color GetColorFor(string name) => name switch {
		"yellow" => new Color(1.00f, 0.85f, 0.10f),
		"green"  => new Color(0.10f, 0.72f, 0.18f),
		"red"    => new Color(0.90f, 0.12f, 0.12f),
		"blue"   => new Color(0.12f, 0.40f, 0.90f),
		_        => Colors.White,
	};
}
