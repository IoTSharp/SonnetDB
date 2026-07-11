using SonnetDB.Contracts;

namespace SonnetDB.Copilot;

/// <summary>
/// 把 OpenAI-compatible 模型清单转换为稳定的 provider-neutral 分组。
/// </summary>
internal static class CopilotModelCatalog
{
    internal const string PlatformDefaultGroup = "platform-default";
    internal const string CustomGroup = "custom";
    internal const string LocalGroup = "local";

    /// <summary>
    /// 生成模型目录。上游未声明分组时，仅把默认项归入平台默认，其余项归入自定义。
    /// </summary>
    /// <param name="items">OpenAI-compatible 模型列表。</param>
    /// <returns>去重并按稳定来源分组的模型目录。</returns>
    internal static ModelCatalog Build(IReadOnlyList<OpenAiModelItem>? items)
    {
        var validItems = (items ?? [])
            .Where(static item => !string.IsNullOrWhiteSpace(item.Id))
            .ToArray();
        var defaultId = validItems
            .FirstOrDefault(static item => item.IsDefault == true)
            ?.Id.Trim()
            ?? validItems.FirstOrDefault()?.Id.Trim()
            ?? string.Empty;

        var candidates = new List<string>(validItems.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var customModels = new List<CopilotModelResponse>();
        var localModels = new List<CopilotModelResponse>();
        CopilotModelResponse? defaultModel = null;

        foreach (var item in validItems)
        {
            var id = item.Id.Trim();
            if (!seen.Add(id))
                continue;

            var model = new CopilotModelResponse(
                id,
                string.IsNullOrWhiteSpace(item.DisplayName) ? id : item.DisplayName.Trim(),
                string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase));
            if (model.IsDefault)
            {
                defaultModel = model;
                continue;
            }

            candidates.Add(model.Id);
            if (string.Equals(item.Group?.Trim(), LocalGroup, StringComparison.OrdinalIgnoreCase))
                localModels.Add(model);
            else
                customModels.Add(model);
        }

        if (defaultModel is not null)
            candidates.Insert(0, defaultModel.Id);

        var groups = new CopilotModelGroupResponse[]
        {
            new(PlatformDefaultGroup, "平台默认模型", defaultModel is null ? [] : [defaultModel]),
            new(CustomGroup, "自定义模型", customModels),
            new(LocalGroup, "本地模型", localModels),
        };
        return new ModelCatalog(defaultId, candidates, groups);
    }

    internal sealed record ModelCatalog(
        string Default,
        IReadOnlyList<string> Candidates,
        IReadOnlyList<CopilotModelGroupResponse> Groups);
}
