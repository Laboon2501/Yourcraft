using Dalamud.Plugin.Services;
using Yourcraft.Models;

namespace Yourcraft.Services;

public sealed class ExperimentalNpcService
{
    private readonly IPluginLog log;

    public ExperimentalNpcService(IPluginLog log)
    {
        this.log = log;
    }

    public void SpawnLocalNpc(CustomNpc npc)
    {
        var appearance = npc.Appearance ?? new CustomNpcAppearance();
        this.log.Information(
            "[ExperimentalNpcService] 请求生成本地 NPC：Id={NpcId}, Name={Name}, AppearanceSource={SourceType}。当前版本只记录日志，不实际生成模型。",
            npc.Id,
            npc.Name,
            appearance.SourceType);

        switch (appearance.SourceType)
        {
            case CustomNpcAppearanceSourceType.GameNpc:
                this.log.Information(
                    "[ExperimentalNpcService] GameNpc 外观：Name={GameNpcName}, BaseId={GameNpcBaseId}, Kind={GameNpcKind}, ModelId={GameNpcModelId}, CustomizeId={GameNpcCustomizeId}",
                    appearance.GameNpcName,
                    appearance.GameNpcBaseId,
                    appearance.GameNpcKind,
                    appearance.GameNpcModelId,
                    appearance.GameNpcCustomizeId);
                break;

            case CustomNpcAppearanceSourceType.GlamourerDesign:
                this.log.Information("[ExperimentalNpcService] Glamourer 外观：DesignId={DesignId}", appearance.GlamourerDesignId);
                break;

            case CustomNpcAppearanceSourceType.MCDF:
                this.log.Information("[ExperimentalNpcService] MCDF 外观：Path={McdfPath}", appearance.McdfPath);
                break;

            case CustomNpcAppearanceSourceType.PenumbraCollection:
                this.log.Information("[ExperimentalNpcService] Penumbra 外观：Collection={Collection}", appearance.PenumbraCollectionName);
                break;
        }
    }

    public void DespawnLocalNpc(string npcId)
    {
        this.log.Information($"[ExperimentalNpcService] 请求删除本地 NPC：{npcId}。当前版本只记录日志，不实际删除模型。");
    }

    public void UpdateLocalNpcPosition(CustomNpc npc)
    {
        this.log.Information($"[ExperimentalNpcService] 请求更新本地 NPC 位置：{npc.Name} ({npc.Id}) -> X {npc.Position.X:F2}, Y {npc.Position.Y:F2}, Z {npc.Position.Z:F2}。当前版本只记录日志。");
    }

    public void DespawnAll()
    {
        this.log.Information("[ExperimentalNpcService] 请求删除全部实验 NPC。当前版本只记录日志，不实际删除模型。");
    }
}
