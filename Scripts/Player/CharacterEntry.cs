// ═══════════════════════════════════════════════════
// CharacterEntry.cs
// Resource that describes one playable character:
// their model scene, server ID, animation offsets,
// and camera adjustments for standing and sitting.
// ═══════════════════════════════════════════════════
using Godot;

/// <summary>
/// Data resource that configures a single playable character variant.
/// Consumed by <see cref="Selector"/> and <see cref="PlayerCameraController"/>
/// to swap models and apply per-character camera / animation offsets.
/// </summary>
[GlobalClass]
public partial class CharacterEntry : Resource
{
	/// <summary>PackedScene containing the character's GLB mesh and AnimationPlayer.</summary>
	[Export] public PackedScene ModelScene;

	/// <summary>Server-side ID sent to the backend to identify this character.</summary>
	[Export] public int ServerId;

	/// <summary>Y-axis rotation (degrees) applied to the root bone in the idle pose
	/// to compensate for Blender's default 90° export offset.</summary>
	[Export] public float IdleRotation = 0f;

	/// <summary>Local position offset added to the base camera position for this character's height/eye level.</summary>
	[Export] public Vector3 CameraOffset = Vector3.Zero;

	/// <summary>Additional local offset applied to the player body when seated on a chair.</summary>
	[Export] public Vector3 SittingOffset = Vector3.Zero;

	/// <summary>Additional camera offset on top of <see cref="CameraOffset"/> while the player is seated.</summary>
	[Export] public Vector3 SittingCameraOffset = Vector3.Zero;
}
