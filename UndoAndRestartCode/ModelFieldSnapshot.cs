using MegaCrit.Sts2.Core.Models;

namespace UndoAndRestartCode;

internal sealed class ModelFieldSnapshot
{
    private readonly ObjectGraphSnapshot _graph;

    private ModelFieldSnapshot(AbstractModel model)
    {
        _graph = ObjectGraphSnapshot.Capture(model);
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
