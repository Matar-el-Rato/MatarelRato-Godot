// ═══════════════════════════════════════════════════
// EnvironmentCollision.cs
// Auto-generates trimesh collision bodies for every
// MeshInstance3D in the subtree at scene start.
// Tag static environment roots with this script so you
// don't have to create collision manually in the editor.
// ═══════════════════════════════════════════════════
using Godot;
using System;

/// <summary>
/// Utility node that walks the scene tree below itself on _Ready and calls
/// <see cref="MeshInstance3D.CreateTrimeshCollision"/> on every mesh it finds.
/// Attach this to the root of any static environment GLB import.
/// </summary>
[Tool]
public partial class EnvironmentCollision : Node
{
	public override void _Ready()
	{
		CreateCollisions(this);
	}

	/// <summary>
	/// Recursively visits every node in the subtree. For each
	/// <see cref="MeshInstance3D"/> found, creates a trimesh <see cref="StaticBody3D"/>
	/// sibling so the geometry is solid at runtime.
	/// </summary>
	private void CreateCollisions(Node node)
	{
		if (node is MeshInstance3D meshInstance)
			meshInstance.CreateTrimeshCollision();

		foreach (Node child in node.GetChildren())
			CreateCollisions(child);
	}
}
