// ═══════════════════════════════════════════════════
// MusicZone.cs
// Area3D that fades a music track in when the player
// enters a box-shaped zone and fades it back out on exit.
// Runs in the editor ([Tool]) to keep the collision gizmo live.
// ═══════════════════════════════════════════════════
using Godot;
using System;

/// <summary>
/// A box-shaped Area3D that plays music when the player is inside,
/// fading in/out smoothly at the boundary.
/// [Tool] makes this run in the Godot editor so the BoxShape gizmo
/// stays visible and updates live when <see cref="ZoneSize"/> is changed.
/// The track is never stopped — only its volume changes — so the playback
/// position is preserved when the player re-enters.
/// </summary>
[Tool]
[GlobalClass]
public partial class MusicZone : Area3D
{
	// ── Exports ───────────────────────────────────────────────────────────────

	/// <summary>The audio stream to play inside this zone.</summary>
	[Export]
	public AudioStream MusicTrack
	{
		get => _musicTrack;
		set { _musicTrack = value; }
	}
	private AudioStream _musicTrack;

	/// <summary>Box size in world-space metres (X = width, Y = height, Z = depth).</summary>
	[Export]
	public Vector3 ZoneSize
	{
		get => _zoneSize;
		set
		{
			_zoneSize = value;
			UpdateShape(); // Live-update the collision gizmo in the editor.
		}
	}
	private Vector3 _zoneSize = new Vector3(10.0f, 4.0f, 10.0f);

	/// <summary>Duration in seconds for the fade-in and fade-out transitions.</summary>
	[Export] public float FadeTime = 0.6f;

	/// <summary>Target volume in dB when fully inside the zone (0 = unity, negative = quieter).</summary>
	[Export(PropertyHint.Range, "-80,0,0.5")] public float MaxVolume = -12.0f;

	/// <summary>Audio bus to route the music through (e.g. "Music" or "Master").</summary>
	[Export] public string Bus = "Master";

	/// <summary>When true, the AudioStreamMP3 loop flag is enabled at runtime.</summary>
	[Export] public bool Loop = true;

	// ── Internals ─────────────────────────────────────────────────────────────

	private CollisionShape3D _collisionShape;
	private AudioStreamPlayer _player;
	private Tween _fadeTween;
	// Counter handles edge cases where multiple physics bodies trigger enter/exit.
	private int _insideCount = 0;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		// Always sync the collision box (editor + runtime).
		UpdateShape();

		if (Engine.IsEditorHint()) return; // Skip runtime-only logic in the editor.

		// Create a persistent audio player that runs at silence until the player enters.
		_player           = new AudioStreamPlayer();
		_player.Stream    = MusicTrack;
		_player.VolumeDb  = -80.0f;
		_player.Bus       = Bus;
		_player.Autoplay  = false;

		if (Loop && MusicTrack is AudioStreamMP3 mp3)
			mp3.Loop = true;

		AddChild(_player);
		// Start immediately at silence so track position is always live.
		_player.Play();

		BodyEntered += OnBodyEntered;
		BodyExited  += OnBodyExited;

		// Layer 2 = Player physics layer; ignore environment geometry on layer 1.
		CollisionMask = 2;
	}

	// ── Shape management ─────────────────────────────────────────────────────

	/// <summary>
	/// Creates or resizes the BoxShape3D child to match <see cref="ZoneSize"/>.
	/// Safe to call in the editor and at runtime.
	/// </summary>
	private void UpdateShape()
	{
		// Reuse an existing shape node before creating a new one.
		if (_collisionShape == null || !IsInstanceValid(_collisionShape))
		{
			_collisionShape = GetNodeOrNull<CollisionShape3D>("MusicZoneShape")
							?? GetNodeOrNull<CollisionShape3D>("MusicZoneShape2");

			// Fallback: accept any CollisionShape3D child.
			if (_collisionShape == null)
			{
				foreach (var child in GetChildren())
				{
					if (child is CollisionShape3D cs)
					{
						_collisionShape = cs;
						break;
					}
				}
			}
		}

		if (_collisionShape == null || !IsInstanceValid(_collisionShape))
		{
			_collisionShape      = new CollisionShape3D();
			_collisionShape.Name = "MusicZoneShape";
			AddChild(_collisionShape);

			// In editor mode, set the owner so the new node is saved with the scene.
			if (Engine.IsEditorHint() && IsInsideTree())
				_collisionShape.Owner = GetTree().EditedSceneRoot;
		}

		var box  = new BoxShape3D();
		box.Size = _zoneSize;
		_collisionShape.Shape = box;
	}

	// ── Audio ─────────────────────────────────────────────────────────────────

	private void OnBodyEntered(Node3D body)
	{
		if (!IsPlayer(body)) return;
		_insideCount++;
		if (_insideCount == 1)
			FadeTo(MaxVolume); // Ramp up — track is already playing at silence.
	}

	private void OnBodyExited(Node3D body)
	{
		if (!IsPlayer(body)) return;
		_insideCount = Mathf.Max(0, _insideCount - 1);
		if (_insideCount == 0)
			FadeTo(-80.0f); // Fade to silence but keep the track running.
	}

	/// <summary>
	/// Fades this zone's track to silence over <paramref name="duration"/> seconds,
	/// preventing any body-exit callback from re-triggering the normal fade.
	/// </summary>
	public void FadeOut(float duration)
	{
		_insideCount = 0;
		FadeTo(-80.0f, duration);
	}

	/// <summary>
	/// Tweens the audio player's volume_db to <paramref name="targetDb"/>.
	/// Kills any in-progress tween first to avoid fighting transitions.
	/// Pass <paramref name="duration"/> to override the exported <see cref="FadeTime"/>.
	/// </summary>
	private void FadeTo(float targetDb, float duration = -1f, Action onFinished = null)
	{
		if (_fadeTween != null && _fadeTween.IsValid())
			_fadeTween.Kill();

		_fadeTween = CreateTween();
		_fadeTween.TweenProperty(_player, "volume_db", targetDb, duration < 0f ? FadeTime : duration)
			.SetTrans(Tween.TransitionType.Cubic)
			.SetEase(Tween.EaseType.InOut);

		if (onFinished != null)
			_fadeTween.Finished += onFinished;
	}

	/// <summary>
	/// Returns true for CharacterBody3D nodes and any node in the "Player" group.
	/// </summary>
	private static bool IsPlayer(Node3D body)
		=> body is CharacterBody3D || body.IsInGroup("Player");
}
