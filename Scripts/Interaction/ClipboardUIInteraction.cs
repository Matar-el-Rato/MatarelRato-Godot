// ═══════════════════════════════════════════════════
// ClipboardUIInteraction.cs
// Bridges 3D physics ray hits on the clipboard mesh to 2D
// mouse events inside its SubViewport, enabling the player
// to click UI controls while focused on the prop.
// ═══════════════════════════════════════════════════
using Godot;
using System;

/// <summary>
/// Attached to a 3D clipboard Node3D that contains a <see cref="SubViewport"/>.
/// When <see cref="FocusController"/> is focused on this clipboard's parent,
/// incoming mouse events are re-mapped from world UV coordinates to viewport
/// pixel coordinates and pushed into the viewport so the embedded UI responds.
/// Keyboard and action events are forwarded directly.
/// </summary>
[Tool]
public partial class ClipboardUIInteraction : Node3D
{
	[Export] public SubViewport    Viewport;
	[Export] public MeshInstance3D Mesh;
	/// <summary>Surface slot on <see cref="Mesh"/> that displays the viewport texture.</summary>
	[Export] public int            SurfaceIndex = 0;

	// Tracks whether the raycast hit the clipboard last frame to detect exit.
	private bool _wasHittingLastFrame = false;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		if (Viewport == null || Mesh == null) return;

		// Ensure the mesh surface has a StandardMaterial3D linked to the viewport texture.
		var mat = Mesh.GetSurfaceOverrideMaterial(SurfaceIndex) as StandardMaterial3D;
		if (mat == null)
		{
			mat = new StandardMaterial3D();
			mat.Transparency = StandardMaterial3D.TransparencyEnum.Alpha;
			mat.ShadingMode  = StandardMaterial3D.ShadingModeEnum.Unshaded;
			Mesh.SetSurfaceOverrideMaterial(SurfaceIndex, mat);
		}
		mat.AlbedoTexture = Viewport.GetTexture();
	}

	// ── Input routing ─────────────────────────────────────────────────────────

	public override void _Input(InputEvent @event)
	{
		if (Viewport == null || Mesh == null) return;

		// Only process input when FocusController is pointing at this clipboard's parent.
		if (FocusController.Instance == null || !FocusController.Instance.IsFocusedOn(GetParent<Node3D>())) return;

		if (@event is InputEventMouse mouseEvent)
		{
			// Mouse events need UV remapping — handled below.
			// We don't mark as handled here so the viewport/unfocus logic can still fire.
			HandleMouseInput(mouseEvent);
		}
		else if (@event is InputEventKey || @event is InputEventAction)
		{
			// Keyboard and action events pass straight through to the viewport.
			Viewport.PushInput(@event);
			GetViewport().SetInputAsHandled();
		}
	}

	/// <summary>
	/// Casts a physics ray from the camera through the current mouse position.
	/// If it hits this clipboard's geometry, converts the 3D hit position to a
	/// UV coordinate and pushes a remapped mouse event into the SubViewport.
	/// Sends an off-screen motion event on miss to clear hover states inside the UI.
	/// </summary>
	private void HandleMouseInput(InputEventMouse @event)
	{
		var camera = GetViewport().GetCamera3D();
		if (camera == null) return;

		Vector2 mousePos = GetViewport().GetMousePosition();
		var from = camera.ProjectRayOrigin(mousePos);
		var to   = from + camera.ProjectRayNormal(mousePos) * 10.0f;

		var spaceState = GetWorld3D().DirectSpaceState;
		var query      = PhysicsRayQueryParameters3D.Create(from, to, 1);
		query.CollideWithAreas = true;
		var result = spaceState.IntersectRay(query);

		Node hitNode = result.Count > 0 ? result["collider"].As<Node>() : null;

		// Walk up the hit hierarchy to check if it belongs to this clipboard instance.
		bool isHitting = false;
		if (hitNode != null)
		{
			Node current = hitNode;
			while (current != null && current != GetTree().Root)
			{
				if (current == this || current == GetParent())
				{
					isHitting = true;
					break;
				}
				current = current.GetParent();
			}
		}

		if (isHitting)
		{
			Vector3 hitPos      = (Vector3)result["position"];
			Vector2 uv          = GetUVAtHit(hitPos);
			Vector2 viewportSize = Viewport.Size;
			Vector2 mappedPos   = new Vector2(uv.X * viewportSize.X, uv.Y * viewportSize.Y);

			// Clone the original event and redirect its position to viewport space.
			var localEvent = (InputEvent)@event.Duplicate();
			if (localEvent is InputEventMouse mouseLocal)
			{
				mouseLocal.Position       = mappedPos;
				mouseLocal.GlobalPosition = mappedPos;
			}
			Viewport.PushInput(localEvent);
			_wasHittingLastFrame = true;
		}
		else if (_wasHittingLastFrame)
		{
			// Send an off-screen motion event to flush any hover state in the viewport UI.
			var exitEvent = new InputEventMouseMotion();
			exitEvent.Position = new Vector2(-100, -100);
			Viewport.PushInput(exitEvent);
			_wasHittingLastFrame = false;
		}
	}

	/// <summary>
	/// Converts a 3D world hit position on the clipboard mesh to a [0,1] UV coordinate.
	/// Reads actual quad dimensions from a <see cref="QuadMesh"/> if available.
	/// </summary>
	private Vector2 GetUVAtHit(Vector3 hitPos)
	{
		Vector3 localPos = Mesh.ToLocal(hitPos);

		float width  = 0.28f;
		float height = 0.4f;

		if (Mesh.Mesh is QuadMesh quad)
		{
			width  = quad.Size.X;
			height = quad.Size.Y;
		}
		else
		{
			GD.PrintErr($"[ClipboardUIInteraction] Mesh.Mesh is NOT QuadMesh! It is {Mesh.Mesh?.GetType().Name}. Falling back to defaults.");
		}

		// Local X maps to U: [-width/2, +width/2] → [0, 1]
		float x = (localPos.X / width) + 0.5f;
		// Local Y maps to V (inverted): [+height/2, -height/2] → [0, 1]
		float y = 0.5f - (localPos.Y / height);

		return new Vector2(Mathf.Clamp(x, 0, 1), Mathf.Clamp(y, 0, 1));
	}
}
