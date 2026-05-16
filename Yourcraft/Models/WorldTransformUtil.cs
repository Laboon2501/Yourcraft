using System.Numerics;

namespace Yourcraft.Models;

public static class WorldTransformUtil
{
    public static Quaternion WorldEulerRadiansToQuaternion(Vector3 eulerRadians)
        => Normalize(Quaternion.CreateFromYawPitchRoll(eulerRadians.Y, eulerRadians.X, eulerRadians.Z));

    public static Vector3 QuaternionToWorldEulerRadians(Quaternion rotation)
    {
        var q = Normalize(rotation);
        var sinrCosp = 2f * (q.W * q.X + q.Y * q.Z);
        var cosrCosp = 1f - 2f * (q.X * q.X + q.Y * q.Y);
        var pitch = MathF.Atan2(sinrCosp, cosrCosp);

        var sinp = 2f * (q.W * q.Y - q.Z * q.X);
        var yaw = MathF.Abs(sinp) >= 1f ? MathF.CopySign(MathF.PI / 2f, sinp) : MathF.Asin(sinp);

        var sinyCosp = 2f * (q.W * q.Z + q.X * q.Y);
        var cosyCosp = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
        var roll = MathF.Atan2(sinyCosp, cosyCosp);

        return new Vector3(pitch, yaw, roll);
    }

    public static Vector3 NormalizeScale(Vector3 scale)
        => new(
            MathF.Max(0.01f, float.IsFinite(scale.X) ? scale.X : 1f),
            MathF.Max(0.01f, float.IsFinite(scale.Y) ? scale.Y : 1f),
            MathF.Max(0.01f, float.IsFinite(scale.Z) ? scale.Z : 1f));

    public static Quaternion Normalize(Quaternion rotation)
    {
        if (!float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W))
            return Quaternion.Identity;

        return rotation.LengthSquared() < 0.0001f ? Quaternion.Identity : Quaternion.Normalize(rotation);
    }
}
