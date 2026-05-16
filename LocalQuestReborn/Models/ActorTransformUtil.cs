using System.Numerics;

namespace LocalQuestReborn.Models;

public static class ActorTransformUtil
{
    public static Vector3 NormalizeRotation(Vector3 rotationEuler)
        => new(0f, float.IsFinite(rotationEuler.Y) ? rotationEuler.Y : 0f, 0f);

    public static Vector3 NormalizeScale(Vector3 scale)
        => new(UniformScaleFrom(scale));

    public static float UniformScaleFrom(Vector3 scale)
    {
        if (float.IsFinite(scale.Y) && scale.Y > 0.01f)
            return scale.Y;
        if (float.IsFinite(scale.X) && scale.X > 0.01f)
            return scale.X;
        if (float.IsFinite(scale.Z) && scale.Z > 0.01f)
            return scale.Z;
        return 1f;
    }

    public static Vector3 SanitizePosition(Vector3 position, Vector3 fallback = default)
        => float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z)
            ? position
            : fallback;
}
