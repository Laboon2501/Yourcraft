using System.Numerics;

namespace Yourcraft.Models;

public readonly record struct WorldTransform(Vector3 WorldPosition, Vector3 WorldEulerRadians, Vector3 WorldScale)
{
    public Quaternion WorldRotation => Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(
        this.WorldEulerRadians.Y,
        this.WorldEulerRadians.X,
        this.WorldEulerRadians.Z));

    public static WorldTransform FromEuler(Vector3 worldPosition, Vector3 worldEulerRadians, Vector3 worldScale)
        => new(worldPosition, worldEulerRadians, WorldTransformUtil.NormalizeScale(worldScale));
}
