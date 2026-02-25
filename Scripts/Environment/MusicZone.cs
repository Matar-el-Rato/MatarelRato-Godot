using Godot;
using System;

/// <summary>
/// A box-shaped zone that plays music when the player is inside,
/// fading in/out quickly at the boundary.
///
/// [Tool] makes this run in the Godot editor so the blue BoxShape gizmo
/// stays visible and updates live when you resize ZoneSize in the inspector.
/// </summary>
[Tool]
[GlobalClass]
public partial class MusicZone : Area3D
{
	private Vector3 _previousSize = Vector3.Zero;

	[Export]
	public AudioStream MusicTrack
	{
		get => _musicTrack;
		set { _musicTrack = value; }
	}
	private AudioStream _musicTrack;

	/// <summary>Box size in world-space meters (X = width, Y = height, Z = depth).</summary>
	[Export]
	public Vector3 ZoneSize
	{
		get => _zoneSize;
		set
		{
			_zoneSize = value;
			UpdateShape(); // live-update the gizmo in the editor
		}
	}
	private Vector3 _zoneSize = new Vector3(10.0f, 4.0f, 10.0f);

	/// <summary>Fade duration in seconds when crossing the boundary.</summary>
	[Export] public float FadeTime = 0.6f;

	/// <summary>Volume in dB when fully inside the zone. 0 = 100%, -10 ~ background music.</summary>
	[Export(PropertyHint.Range, "-80,0,0.5")] public float MaxVolume = -12.0f;

	/// <summary>Mixer bus name. Create a Bus called e.g. "Music" in the Audio panel to control it separately.</summary>
	[Export] public string Bus = "Master";

	/// <summary>Whether the track loops.</summary>
	[Export] public bool Loop = true;

	// ─── internals ───────────────────────────────────────────────────────────
	private CollisionShape3D _collisionShape;
	private AudioStreamPlayer _player;
	private Tween _fadeTween;
	private int _insideCount = 0;

	public override void _Ready()
	{
		// Always maintain the visual collision box (editor + runtime)
		UpdateShape();

		if (Engine.IsEditorHint()) return; // skip runtime logic in editor

		// ── Audio player ─────────────────────────────────────────────────────
		_player = new AudioStreamPlayer();
		_player.Stream = MusicTrack;
		_player.VolumeDb = -80.0f;
		_player.Bus = Bus;
		_player.Autoplay = false;
		if (Loop && MusicTrack is AudioStreamMP3 mp3)
			mp3.Loop = true;
		AddChild(_player);
		// Start playing immediately at silence — we only volume-fade, never Stop(),
		// so the track position is preserved across zone exits/entries.
		_player.Play();

		// ── Signals ──────────────────────────────────────────────────────────
		BodyEntered += OnBodyEntered;
		BodyExited  += OnBodyExited;
		CollisionMask = 1; // player layer
	}

	// ─── Shape ───────────────────────────────────────────────────────────────

	private void UpdateShape()
	{
		// Reuse existing shape node or create a new one
		if (_collisionShape == null)
			_collisionShape = GetNodeOrNull<CollisionShape3D>("MusicZoneShape");

		if (_collisionShape == null || !IsInstanceValid(_collisionShape))
		{
			_collisionShape = new CollisionShape3D();
			_collisionShape.Name = "MusicZoneShape";
			AddChild(_collisionShape);

			// In-editor: make the shape node part of the saved scene
			if (Engine.IsEditorHint() && IsInsideTree())
				_collisionShape.Owner = GetTree().EditedSceneRoot;
		}

		var box = new BoxShape3D();
		box.Size = _zoneSize;
		_collisionShape.Shape = box;
	}

	// ─── Audio ───────────────────────────────────────────────────────────────

	private void OnBodyEntered(Node3D body)
	{
		if (!IsPlayer(body)) return;
		_insideCount++;
		if (_insideCount == 1)
			FadeTo(MaxVolume); // ramp up — track is already running
	}

	private void OnBodyExited(Node3D body)
	{
		if (!IsPlayer(body)) return;
		_insideCount = Mathf.Max(0, _insideCount - 1);
		if (_insideCount == 0)
			FadeTo(-80.0f); // silence only — track keeps playing to preserve position
	}

	private void FadeTo(float targetDb, Action onFinished = null)
	{
		if (_fadeTween != null && _fadeTween.IsValid())
			_fadeTween.Kill();
		_fadeTween = CreateTween();
		_fadeTween.TweenProperty(_player, "volume_db", targetDb, FadeTime)
			.SetTrans(Tween.TransitionType.Cubic)
			.SetEase(Tween.EaseType.InOut);
		if (onFinished != null)
			_fadeTween.Finished += onFinished;
	}

	private static bool IsPlayer(Node3D body)
		=> body is CharacterBody3D || body.IsInGroup("Player");
}
