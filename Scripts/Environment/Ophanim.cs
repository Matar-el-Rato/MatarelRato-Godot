using Godot;

public partial class Ophanim : Node3D
{
    public override void _Ready()
    {
        var animPlayer = FindAnimationPlayer(this);
        if (animPlayer != null && animPlayer.HasAnimation("Animation"))
        {
            var anim = animPlayer.GetAnimation("Animation");
            anim.LoopMode = Animation.LoopModeEnum.Linear;
            animPlayer.Play("Animation");
        }
    }

    private AnimationPlayer FindAnimationPlayer(Node node)
    {
        if (node is AnimationPlayer ap) return ap;
        foreach (Node child in node.GetChildren())
        {
            var found = FindAnimationPlayer(child);
            if (found != null) return found;
        }
        return null;
    }
}
