using Godot;
using System;

[Tool]
public partial class EnvironmentCollision : Node
{
	public override void _Ready()
	{
		// We only want to run this once or when the scene starts.
		// It generates collisions for all children meshes.
		CreateCollisions(this);
	}

	private void CreateCollisions(Node node)
	{
		if (node is MeshInstance3D meshInstance)
		{
			// Create a trimesh collision sibling
			meshInstance.CreateTrimeshCollision();
		}

		foreach (Node child in node.GetChildren())
		{
			CreateCollisions(child);
		}
	}
}
