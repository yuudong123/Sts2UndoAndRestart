using MegaCrit.Sts2.Core.Models;

namespace UndoAndRedoForkCode;

internal sealed class ModelFieldSnapshot
{
    private readonly SnapshotGraph _graph;

    private ModelFieldSnapshot(AbstractModel model)
    {
        _graph = SnapshotGraph.Capture(model);
    }

    public static ModelFieldSnapshot Capture(AbstractModel model)
    {
        return new ModelFieldSnapshot(model);
    }

    public void Restore(AbstractModel model)
    {
        _graph.Restore(model);
    }
}
