using Godot;

[GlobalClass]
public partial class CharacterEntry : Resource
{
	[Export] public PackedScene ModelScene;
	[Export] public int ServerId;
}
