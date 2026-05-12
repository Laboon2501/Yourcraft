using System.Numerics;

namespace LocalQuestReborn.Models;

public sealed class LocalLayoutObjectInstance
{
    public string Id { get; set; } = string.Empty;

    public string InstanceId
    {
        get => this.Id;
        set => this.Id = value;
    }

    public string TemplateSourceSlotAddress { get; set; } = string.Empty;

    public string SourceResourcePath { get; set; } = string.Empty;

    public string OriginalResourcePath { get; set; } = string.Empty;

    public string CurrentResourcePath { get; set; } = string.Empty;

    public string CustomModelPath { get; set; } = string.Empty;

    public bool ModelOverrideApplied { get; set; }

    public string OriginalModelResourcePath { get; set; } = string.Empty;

    public string OccupiedSlotAddress { get; set; } = string.Empty;

    public LocalLayoutTransformMode TransformMode { get; set; } = LocalLayoutTransformMode.VisualOnly;

    public string GraphicsObjectAddress { get; set; } = string.Empty;

    public int VisualTransformOffset { get; set; } = 0x20;

    public Vector3 OccupiedSlotOriginalPosition { get; set; }

    public Quaternion OccupiedSlotOriginalRotation { get; set; } = Quaternion.Identity;

    public Vector3 OccupiedSlotOriginalScale { get; set; } = Vector3.One;

    public Vector3 CurrentPosition { get; set; }

    public Quaternion CurrentRotation { get; set; } = Quaternion.Identity;

    public Vector3 CurrentRotationEuler { get; set; }

    public Vector3 CurrentScale { get; set; } = Vector3.One;

    public string OriginalVisualTransform { get; set; } = "未读取";

    public string OriginalLayoutTransform { get; set; } = "未读取";

    public Vector3 OriginalVisualTranslation { get; set; }

    public Vector3 OriginalVisualPosition { get; set; }

    public Quaternion OriginalVisualRotation { get; set; } = Quaternion.Identity;

    public Vector3 OriginalVisualScale { get; set; } = Vector3.One;

    public Vector3 OriginalLayoutPosition { get; set; }

    public Quaternion OriginalLayoutRotation { get; set; } = Quaternion.Identity;

    public Vector3 OriginalLayoutScale { get; set; } = Vector3.One;

    public Vector3 CurrentVisualTranslation { get; set; }

    public Matrix4x4 OriginalVisualMatrix { get; set; } = Matrix4x4.Identity;

    public Matrix4x4 CurrentVisualMatrix { get; set; } = Matrix4x4.Identity;

    public bool VisualOnlyVerified { get; set; }

    public bool Visible { get; set; }

    public bool IsOccupied { get; set; }

    public bool IsRestored { get; set; }

    public bool IsDuplicate { get; set; }

    public bool IsInvalid { get; set; }

    public bool HasCollisionMoved { get; set; }

    public bool CanRestore { get; set; }

    public string LastReadback { get; set; } = "未读取";

    public string LastError { get; set; } = string.Empty;

    public string LastModelOverrideResult { get; set; } = string.Empty;

    public string LastModelOverrideError { get; set; } = string.Empty;

    public string BeforeModelPath { get; set; } = string.Empty;

    public string TargetModelPath { get; set; } = string.Empty;

    public string AfterModelPath { get; set; } = string.Empty;

    public string ModelResourceHandleAddress { get; set; } = string.Empty;

    public string OriginalModelResourceHandleAddress { get; set; } = string.Empty;

    public string SetModelReturnValue { get; set; } = string.Empty;

    public string LastSetModelException { get; set; } = string.Empty;

    public string ModelVisibilityReadback { get; set; } = string.Empty;

    public string ModelTransformReadback { get; set; } = string.Empty;

    public string ModelResourceHandleDump { get; set; } = string.Empty;

    public string ModelResourceHandleVTable { get; set; } = string.Empty;

    public string ModelResourceHandleType { get; set; } = string.Empty;

    public string ModelResourceHandleFileType { get; set; } = string.Empty;

    public string ModelResourceHandleLoadState { get; set; } = string.Empty;

    public string ModelResourceHandleId { get; set; } = string.Empty;

    public string ModelResourceCategoryReadback { get; set; } = string.Empty;

    public string ModelResourceCategoryGuess { get; set; } = string.Empty;

    public string ModelResourceCategoryConfidence { get; set; } = string.Empty;

    public string SetModelSignatureReadback { get; set; } = string.Empty;

    public string ManualVisualConfirmation { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;
}
