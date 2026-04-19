using Godot;

public partial class Ophanim : Node3D
{
    [Export] public float AnimationSpeed = 1.75f;

    public override void _Ready()
    {
        var animPlayer = FindAnimationPlayer(this);
        if (animPlayer != null && animPlayer.HasAnimation("Animation"))
        {
            var anim = animPlayer.GetAnimation("Animation");
            anim.LoopMode = Animation.LoopModeEnum.Linear;
            animPlayer.SpeedScale = AnimationSpeed;
            animPlayer.Play("Animation");
        }

        SetupAudio("DroneAudio");
        SetupAudio("WingFlapAudio");
    }

    private void SetupAudio(string nodeName)
    {
        var player = GetNodeOrNull<AudioStreamPlayer3D>(nodeName);
        if (player == null) return;
        player.Finished += () => player.Play();
        player.Play();
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
