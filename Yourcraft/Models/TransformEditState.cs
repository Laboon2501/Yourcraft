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
        if (string.Equals(this.BoundRuntimeId, editable.RuntimeId, StringComparison.Ordinal) &&
            this.LastSyncedGeneration == generation)
        {
            return;
        }

        this.BoundRuntimeId = editable.RuntimeId;
        this.PositionInput = editable.Transform.WorldPosition;
        this.EulerInput = editable.Transform.WorldEulerRadians;
        this.ScaleInput = editable.Transform.WorldScale == Vector3.Zero ? Vector3.One : editable.Transform.WorldScale;
        this.LastSyncedGeneration = generation;
    }

    public WorldTransform ToWorldTransform()
        => WorldTransform.FromEuler(this.PositionInput, this.EulerInput, this.ScaleInput);
}
