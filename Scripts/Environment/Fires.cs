// ═══════════════════════════════════════════════════
// Fires.cs
// Proximity-driven fade for the entrance fire holders.
// Each Holder child fades in when the player enters
// FadeRadius metres and fades out when they leave.
// Call DisableAll() once the pizzeria doors open.
// ═══════════════════════════════════════════════════
using Godot;
using System.Collections.Generic;

public partial class Fires : Node3D
{
	[Export] public float FadeRadius       = 5.0f;
	[Export] public float FadeInDuration   = 0.5f;
	[Export] public float FadeOutDuration  = 1.0f;
	/// <summary>When this door opens, all fires are permanently disabled.</summary>
	[Export] public Door  PizzeriaDoor;

	private class FireEntry
	{
		public Node3D         Holder;
		public OmniLight3D    Light;
		public GpuParticles3D Sparks;
		public MeshInstance3D Mesh;
		public float          OrigEnergy;
		public bool           IsVisible;
		public Tween          ActiveTween;
	}

	private readonly List<FireEntry> _fires    = new();
	private bool   _disabled = false;
	private Node3D _player;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		foreach (Node child in GetChildren())
		{
			if (child is not Node3D holder) continue;

			var light  = holder.FindChild("Light",      true, false) as OmniLight3D;
			var sparks = holder.FindChild("Sparks",     true, false) as GpuParticles3D;
			var mesh   = holder.FindChild("Fire_Drop2", true, false) as MeshInstance3D;

			float origEnergy = light?.LightEnergy ?? 0.175f;

			// Start fully hidden.
			if (light  != null) light.LightEnergy  = 0f;
			if (sparks != null) { sparks.AmountRatio = 0f; sparks.Emitting = false; }
			if (mesh   != null) mesh.Visible         = false;

			_fires.Add(new FireEntry
			{
				Holder    = holder,
				Light     = light,
				Sparks    = sparks,
				Mesh      = mesh,
				OrigEnergy = origEnergy,
				IsVisible  = false,
			});
		}
	}

	// ── Per-frame proximity check ─────────────────────────────────────────────

	public override void _Process(double delta)
	{
		if (!_disabled && IsInstanceValid(PizzeriaDoor) && PizzeriaDoor.IsOpen)
			DisableAll();

		if (_disabled) return;

		if (_player == null || !IsInstanceValid(_player))
			_player = GetTree().GetFirstNodeInGroup("player") as Node3D;
		if (_player == null) return;

		Vector3 playerPos = _player.GlobalPosition;

		foreach (var fire in _fires)
		{
			if (!IsInstanceValid(fire.Holder)) continue;

			bool inside = fire.Holder.GlobalPosition.DistanceTo(playerPos) <= FadeRadius;

			if (inside && !fire.IsVisible)
			{
				fire.IsVisible = true;
				StartFade(fire, fadeIn: true);
			}
			else if (!inside && fire.IsVisible)
			{
				fire.IsVisible = false;
				StartFade(fire, fadeIn: false);
			}
		}
	}

	// ── Fade helpers ──────────────────────────────────────────────────────────

	private void StartFade(FireEntry fire, bool fadeIn)
	{
		fire.ActiveTween?.Kill();

		var tween = CreateTween().SetParallel(true);

		if (fadeIn)
		{
			if (fire.Mesh   != null) fire.Mesh.Visible     = true;
			if (fire.Sparks != null) fire.Sparks.Emitting  = true;

			if (fire.Light != null)
				tween.TweenProperty(fire.Light, "light_energy", fire.OrigEnergy, FadeInDuration)
					 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);

			if (fire.Sparks != null)
				tween.TweenProperty(fire.Sparks, "amount_ratio", 1.0f, FadeInDuration)
					 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);

			PlayBurnSound(fire.Holder.GlobalPosition);
		}
		else
		{
			if (fire.Light != null)
				tween.TweenProperty(fire.Light, "light_energy", 0f, FadeOutDuration)
					 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);

			if (fire.Sparks != null)
				tween.TweenProperty(fire.Sparks, "amount_ratio", 0f, FadeOutDuration)
					 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);

			// Hide mesh and stop emitting once the fade completes.
			var mesh   = fire.Mesh;
			var sparks = fire.Sparks;
			tween.Finished += () =>
			{
				if (IsInstanceValid(mesh))   mesh.Visible    = false;
				if (IsInstanceValid(sparks)) sparks.Emitting = false;
			};
		}

		fire.ActiveTween = tween;
	}

	private void PlayBurnSound(Vector3 worldPos)
	{
		var clip = GD.Load<AudioStream>("res://Assets/Sound FX/burnfx.wav");
		if (clip == null) return;
		var sfx = new AudioStreamPlayer3D
		{
			Stream      = clip,
			VolumeDb    = -10f,
			MaxDistance = 8f,
		};
		AddChild(sfx);
		sfx.GlobalPosition = worldPos;
		sfx.Play();
		sfx.Finished += () => { if (IsInstanceValid(sfx)) sfx.QueueFree(); };
	}

	// ── Public API ────────────────────────────────────────────────────────────

	/// <summary>
	/// Fades out all fires and permanently disables proximity detection.
	/// Call once the pizzeria entrance doors have been opened.
	/// </summary>
	public void DisableAll()
	{
		_disabled = true;
		foreach (var fire in _fires)
		{
			fire.ActiveTween?.Kill();
			fire.IsVisible = false;
			if (fire.Light  != null) fire.Light.LightEnergy   = 0f;
			if (fire.Sparks != null) { fire.Sparks.AmountRatio = 0f; fire.Sparks.Emitting = false; }
			if (fire.Mesh   != null) fire.Mesh.Visible          = false;
		}
	}
}
