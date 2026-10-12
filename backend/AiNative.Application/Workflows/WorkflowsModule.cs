using AiNative.Application.Access;
using AiNative.Application.Modules;

namespace AiNative.Application.Workflows;

/// <summary>
/// 自动化工作流：事件、定时或手动触发，按顺序执行平台动作、派活给智能体、调用模型处理文本、等待。
/// </summary>
internal sealed class WorkflowsModule : IPlatformModule
{
    public const string ModuleKey = "workflows";

    public string Key => ModuleKey;

    public string Name => "自动化工作流";

    public string Description => "把固定流程交给平台自动跑：任务创建、状态变化、审批结论或定时触发，按顺序执行平台动作、派活给智能体并等它完成、让模型总结分类，全程留痕可追溯。";

    public bool IsCore => false;

    public bool EnabledByDefault => true;

    public IReadOnlyList<FeatureDefinition> Features { get; } =
    [
        new(Access.Features.Workflows.View, "查看工作流与运行记录", ModuleKey),
        new(Access.Features.Workflows.Manage, "创建、编辑、启停工作流", ModuleKey),
        new(Access.Features.Workflows.Run, "手动运行与取消工作流", ModuleKey),
    ];
}
