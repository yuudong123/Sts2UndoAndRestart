using Godot;

namespace UndoAndRestartCode;

internal static class GodotFrames
{
    /// <summary>
    /// Resumes on the main thread after the next SceneTree process frame.
    /// Every polling loop in the mod waits through this single path so they all
    /// observe the same frame boundary.
    /// </summary>
    public static async Task NextProcessFrameAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Engine.GetMainLoop() is SceneTree sceneTree)
        {
            await sceneTree.ToSignal(sceneTree, SceneTree.SignalName.ProcessFrame);
        }
        else
        {
            await Task.Delay(16, cancellationToken);
        }
    }

    public static async Task ProcessFramesAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        for (int frame = 0; frame < count; frame++)
        {
            await NextProcessFrameAsync(cancellationToken);
        }
    }
}
