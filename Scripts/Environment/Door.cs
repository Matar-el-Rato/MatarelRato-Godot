using Godot;
using System;

public partial class Door : Node3D, IInteractable
{
	[Export] public float OpenAngle = 120.0f;
	[Export] public float AnimationDuration = 0.5f;
	[Export] public NodePath LinkedDoorPath;

	private bool _isOpen = false;
	private Vector3 _closedRotation;
	private Vector3 _openRotation;
	private Door _linkedDoor;
	private bool _isProcessingLinked = false;
	private bool _isAnimating = false;
	
	private ShaderMaterial _highlightMaterial;
	private static readonly string SHADER_PATH = "res://Shaders/highlight.gdshader";

	public override void _Ready()
	{
		_closedRotation = Rotation;
		_openRotation = new Vector3(Rotation.X, Rotation.Y + Mathf.DegToRad(OpenAngle), Rotation.Z);
		
		if (!LinkedDoorPath.IsEmpty)
		{
			_linkedDoor = GetNodeOrNull<Door>(LinkedDoorPath);
		}

		// Generate collisions for all child meshes
		CreateCollisions(this);

		// Prepare highlight material
		_highlightMaterial = new ShaderMaterial();
		_highlightMaterial.Shader = GD.Load<Shader>(SHADER_PATH);
	}

	private void CreateCollisions(Node node)
	{
		if (node is MeshInstance3D meshInstance)
		{
			meshInstance.CreateTrimeshCollision();
		}

		foreach (Node child in node.GetChildren())
		{
			CreateCollisions(child);
		}
	}

	public void Interact()
	{
		if (_isAnimating) return;

		_isOpen = !_isOpen;
		_isAnimating = true;

		Vector3 targetRotation = _isOpen ? _openRotation : _closedRotation;
		
		Tween tween = GetTree().CreateTween();
		tween.TweenProperty(this, "rotation", targetRotation, AnimationDuration)
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.Out);
		
		tween.Finished += () => _isAnimating = false;

		// Sync with linked door immediately (no delay)
		if (_linkedDoor != null && !_isProcessingLinked)
		{
			_isProcessingLinked = true;
			_linkedDoor.Interact();
			_isProcessingLinked = false;
		}
	}

	public void OnFocus()
	{
		ApplyHighlight(true);
		
		if (_linkedDoor != null && !_isProcessingLinked)
		{
			_isProcessingLinked = true;
			_linkedDoor.OnFocus();
			_isProcessingLinked = false;
		}
	}

	public void OnBlur()
	{
		ApplyHighlight(false);
		
		if (_linkedDoor != null && !_isProcessingLinked)
		{
			_isProcessingLinked = true;
			_linkedDoor.OnBlur();
			_isProcessingLinked = false;
		}
	}

	private void ApplyHighlight(bool active)
	{
		foreach (Node child in GetChildren())
		{
			if (child is MeshInstance3D mesh)
			{
				if (active)
				{
					mesh.MaterialOverlay = _highlightMaterial;
				}
				else
				{
					mesh.MaterialOverlay = null;
				}
			}
		}
	}
}
