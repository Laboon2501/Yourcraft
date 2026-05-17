using System.Numerics;

namespace Yourcraft.Models;

public sealed class TransformEditState
{
    public string BoundRuntimeId { get; set; } = string.Empty;

    public Vector3 PositionInput { get; set; }

    public Vector3 EulerInput { get; set; }

    public Vector3 ScaleInput { get; set; } = Vector3.One;

    public uint LastSyncedGeneration { get; set; }

    public void Bind(SceneEditableRef editable, uint generation)
    {
        var sameBinding = string.Equals(this.BoundRuntimeId, editable.RuntimeId, StringComparison.Ordinal);
        if (sameBinding && this.LastSyncedGeneration == generation)
        {
            return;
        }

        var previousEuler = this.EulerInput;
        this.BoundRuntimeId = editable.RuntimeId;
        this.PositionInput = editable.Transform.WorldPosition;
        this.EulerInput = sameBinding &&
                          WorldTransformUtil.RotationsEquivalent(previousEuler, editable.Transform.WorldEulerRadians)
            ? previousEuler
            : editable.Transform.WorldEulerRadians;
        this.ScaleInput = editable.Transform.WorldScale == Vector3.Zero ? Vector3.One : editable.Transform.WorldScale;
        this.LastSyncedGeneration = generation;
    }

    public WorldTransform ToWorldTransform()
        => WorldTransform.FromEuler(this.PositionInput, this.EulerInput, this.ScaleInput);
}
