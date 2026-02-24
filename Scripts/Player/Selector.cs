using Godot;
using System;
using System.Collections.Generic;

public partial class Selector : Node
{
	[Export] public CharacterEntry[] Entries;
	[Export] public NodePath PlayerControllerPath = "..";
	private PlayerCameraController _playerController;

	private int _currentIndex = 0;

	public override void _Ready()
	{
		if (PlayerControllerPath != null)
		{
			_playerController = GetNodeOrNull<PlayerCameraController>(PlayerControllerPath);
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		bool actionPressed = false;
		if (InputMap.HasAction("cycle_character"))
		{
			actionPressed = @event.IsActionPressed("cycle_character");
		}

		if (actionPressed || ( @event is InputEventKey ek && ek.Pressed && ek.Keycode == Key.P))
		{
			CycleCharacter();
		}
	}

	private void CycleCharacter()
	{
		if (Entries == null || Entries.Length == 0)
		{
			GD.PrintErr("Selector Error: Entries list is empty or NULL.");
			return;
		}
		
		if (_playerController == null)
		{
			GD.PrintErr("Selector Error: PlayerController is NULL.");
			return;
		}

		_currentIndex = (_currentIndex + 1) % Entries.Length;
		var entry = Entries[_currentIndex];
		
		if (entry?.ModelScene == null)
		{
			GD.PrintErr($"Selector Error: Character at index {_currentIndex} has no ModelScene.");
			return;
		}

		string modelName = System.IO.Path.GetFileNameWithoutExtension(entry.ModelScene.ResourcePath);
		_playerController.SwapCharacter(entry.ModelScene);
	}
}
