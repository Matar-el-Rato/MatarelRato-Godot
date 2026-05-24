using Godot;
using System.Collections.Generic;

public partial class LightFlicker : Node3D
{
	[Export] public float MinEnergy        = 0.02f;
	[Export] public float MaxEnergy        = 0.25f;
	[Export] public float MinInterval      = 0.1f;
	[Export] public float MaxInterval      = 0.5f;
	[Export] public float IntenseMinEnergy   = 0.0f;
	[Export] public float IntenseMaxEnergy   = 1.2f;
	[Export] public float IntenseMinInterval = 0.02f;
	[Export] public float IntenseMaxInterval = 0.06f;

	private OmniLight3D[]          _lights;
	private float[]                _timers;
	private RandomNumberGenerator  _rng = new();
	private float                  _activeMinEnergy;
	private float                  _activeMaxEnergy;
	private float                  _activeMinInterval;
	private float                  _activeMaxInterval;

	public override void _Ready()
	{
		var list = new List<OmniLight3D>();
		foreach (Node child in GetChildren())
			if (child is OmniLight3D light)
				list.Add(light);

		_lights = list.ToArray();
		_timers = new float[_lights.Length];
		SetIntenseMode(false);
	}

	public void SetIntenseMode(bool intense)
	{
		_activeMinEnergy   = intense ? IntenseMinEnergy   : MinEnergy;
		_activeMaxEnergy   = intense ? IntenseMaxEnergy   : MaxEnergy;
		_activeMinInterval = intense ? IntenseMinInterval : MinInterval;
		_activeMaxInterval = intense ? IntenseMaxInterval : MaxInterval;
	}

	public override void _Process(double delta)
	{
		for (int i = 0; i < _lights.Length; i++)
		{
			_timers[i] -= (float)delta;
			if (_timers[i] <= 0f)
			{
				_lights[i].LightEnergy = _rng.RandfRange(_activeMinEnergy, _activeMaxEnergy);
				_timers[i] = _rng.RandfRange(_activeMinInterval, _activeMaxInterval);
			}
		}
	}
}
