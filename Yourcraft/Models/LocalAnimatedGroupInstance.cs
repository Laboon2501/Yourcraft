namespace Yourcraft.Models;

public sealed class LocalAnimatedGroupInstance
{
    public string GroupId { get; set; } = string.Empty;

    public string SourceSharedGroup { get; set; } = string.Empty;

    public List<string> ChildInstanceIds { get; set; } = [];

    public List<string> CarrierSlotAddresses { get; set; } = [];

    public bool PlaybackEnabled { get; set; }

    public bool IsRestoring { get; set; }

    public bool IsRestored { get; set; }

    public string RestoreStatus { get; set; } = string.Empty;

    public string LastError { get; set; } = string.Empty;
}
