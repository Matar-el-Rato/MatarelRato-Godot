using Godot;

[GlobalClass]
public partial class CharacterEntry : Resource
{
	[Export] public PackedScene ModelScene;
	[Export] public int ServerId;
	[Export] public float IdleRotation = 0f;
	[Export] public Vector3 CameraOffset = Vector3.Zero;
	[Export] public Vector3 SittingOffset = Vector3.Zero;
	[Export] public Vector3 SittingCameraOffset = Vector3.Zero;
}
