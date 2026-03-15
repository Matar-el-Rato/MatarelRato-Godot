// ═══════════════════════════════════════════════════
// Mirror.cs
// Real-time planar mirror using a SubViewport + shader.
// Reflects the scene through a mathematically correct
// reflection matrix (I − 2·n⊗n) every frame.
// Freezes the viewport when the player is far away.
// ═══════════════════════════════════════════════════
using Godot;

/// <summary>
/// Procedural planar mirror. Place this Node3D in the scene and set the exports;
/// all required children (SubViewport, Camera3D, MeshInstance3D) are created
/// automatically if they don't already exist.
/// The mirror camera is updated each frame by reflecting the player camera's
/// transform through the mirror plane, then a frustum is fitted tightly around
/// the quad so only visible pixels are rendered.
/// </summary>
public partial class Mirror : Node3D
{
	[Export] public Vector2 MirrorSize    = new Vector2(1.0f, 2.0f);
	/// <summary>Viewport resolution per world-space metre. Higher = sharper, more expensive.</summary>
	[Export] public int     PixelsPerUnit = 200;
	/// <summary>Distance at which the mirror stops updating to save GPU time.</summary>
	[Export] public float   FreezeDistance = 50.0f;
	[Export] public float   CullNear       = 0.05f;
	[Export] public float   CullFar        = 50.0f;

	private Camera3D       _mirrorCamera;
	private SubViewport    _viewport;
	private MeshInstance3D _mirrorMesh;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		// Create children on demand so the mirror works even when added as a bare node.
		EnsureViewport();
		EnsureCamera();
		EnsureMesh();

		// Share the main scene's World3D so the mirror camera renders the real environment.
		_viewport.World3D = GetViewport().World3D;

		ApplySize();

		// Wire the viewport's render texture to a shader material on the quad.
		var shader = GD.Load<Shader>("res://Shaders/mirror.gdshader");
		var mat    = new ShaderMaterial();
		mat.Shader = shader;
		mat.SetShaderParameter("mirror_texture", _viewport.GetTexture());
		_mirrorMesh.SetSurfaceOverrideMaterial(0, mat);
	}

	// ── Child setup ───────────────────────────────────────────────────────────

	private void EnsureViewport()
	{
		_viewport = GetNodeOrNull<SubViewport>("MirrorViewport");
		if (_viewport != null) return;

		_viewport = new SubViewport { Name = "MirrorViewport" };
		_viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible;
		AddChild(_viewport);
	}

	private void EnsureCamera()
	{
		_mirrorCamera = GetNodeOrNull<Camera3D>("MirrorViewport/MirrorCamera");
		if (_mirrorCamera == null)
		{
			_mirrorCamera = new Camera3D { Name = "MirrorCamera" };
			_viewport.AddChild(_mirrorCamera);
		}
		_mirrorCamera.KeepAspect = Camera3D.KeepAspectEnum.Width;
	}

	private void EnsureMesh()
	{
		_mirrorMesh = GetNodeOrNull<MeshInstance3D>("MirrorMesh");
		if (_mirrorMesh == null)
		{
			_mirrorMesh = new MeshInstance3D { Name = "MirrorMesh", Mesh = new QuadMesh() };
			AddChild(_mirrorMesh);
		}
		else if (_mirrorMesh.Mesh is not QuadMesh)
		{
			_mirrorMesh.Mesh = new QuadMesh();
		}
	}

	private void ApplySize()
	{
		if (_mirrorMesh?.Mesh is QuadMesh q)
			q.Size = MirrorSize;

		if (_viewport != null)
			_viewport.Size = new Vector2I(
				Mathf.Max(1, (int)(MirrorSize.X * PixelsPerUnit)),
				Mathf.Max(1, (int)(MirrorSize.Y * PixelsPerUnit)));
	}

	// ── Per-frame reflection ──────────────────────────────────────────────────

	public override void _Process(double delta)
	{
		if (_mirrorCamera == null || _mirrorMesh == null) return;

		Camera3D playerCamera = GetViewport().GetCamera3D();
		if (playerCamera == null) return;

		// Disable rendering when far away to save GPU.
		if (GlobalPosition.DistanceTo(playerCamera.GlobalPosition) >= FreezeDistance)
		{
			_viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
			return;
		}
		_viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible;

		// Mirror normal = the quad's +Z world axis.
		Vector3 mirrorNormal = _mirrorMesh.GlobalBasis.Z;

		// Step 1: reflect the player camera transform through the mirror plane.
		Transform3D reflectT = GetReflectionTransform(mirrorNormal, _mirrorMesh.GlobalPosition);
		_mirrorCamera.GlobalTransform = reflectT * playerCamera.GlobalTransform;

		// Step 2: force the mirror camera to look at the midpoint between itself and
		// the player, which keeps the image perpendicular to the mirror surface.
		Vector3 lookTarget = (_mirrorCamera.GlobalPosition / 2f) + (playerCamera.GlobalPosition / 2f);
		if (lookTarget.DistanceTo(_mirrorCamera.GlobalPosition) > 0.001f)
			_mirrorCamera.GlobalTransform = _mirrorCamera.GlobalTransform
				.LookingAt(lookTarget, _mirrorMesh.GlobalBasis.Y);

		// Step 3: fit the frustum exactly to the mirror quad to avoid wasted rendering.
		Vector3 camToMirror   = _mirrorMesh.GlobalPosition - _mirrorCamera.GlobalPosition;
		float   near          = Mathf.Abs(camToMirror.Dot(mirrorNormal)) + CullNear;
		float   far           = camToMirror.Length() + CullFar;

		// Compute the lateral offset of the mirror centre in the camera's local space.
		Vector3 offsetLocal   = _mirrorCamera.GlobalBasis.Inverse() * camToMirror;
		Vector2 frustumOffset = new Vector2(offsetLocal.X, offsetLocal.Y);
		_mirrorCamera.SetFrustum(MirrorSize.X, frustumOffset, near, far);
	}

	// ── Reflection matrix ─────────────────────────────────────────────────────

	/// <summary>
	/// Builds the Householder reflection transform I − 2·n⊗n for a plane with
	/// outward normal <paramref name="n"/> passing through <paramref name="offset"/>.
	/// </summary>
	private static Transform3D GetReflectionTransform(Vector3 n, Vector3 offset)
	{
		Vector3 bx = new Vector3(1, 0, 0) - 2f * new Vector3(n.X*n.X, n.X*n.Y, n.X*n.Z);
		Vector3 by = new Vector3(0, 1, 0) - 2f * new Vector3(n.Y*n.X, n.Y*n.Y, n.Y*n.Z);
		Vector3 bz = new Vector3(0, 0, 1) - 2f * new Vector3(n.Z*n.X, n.Z*n.Y, n.Z*n.Z);
		return new Transform3D(new Basis(bx, by, bz), 2f * n.Dot(offset) * n);
	}
}
