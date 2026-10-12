using System.Text.Json;
using AiNative.Domain.Common;

namespace AiNative.Domain.Workflows;

public enum WorkflowTriggerKind
{
    /// <summary>
    /// 只在有人（或智能体、对话）手动运行时执行。
    /// </summary>
    Manual = 0,

    /// <summary>
    /// 平台事件发生时执行，例如任务创建、状态变化、审批结论。
    /// </summary>
    Event = 1,

    /// <summary>
    /// 按 cron 表达式定时执行。
    /// </summary>
    Schedule = 2,

    /// <summary>
    /// 外部系统向工作流专属的 Webhook 地址发送请求时执行，请求内容作为触发数据。
    /// </summary>
    Webhook = 3,
}

public enum WorkflowStepKind
{
    /// <summary>
    /// 执行一个平台动作（与界面、智能体、MCP 共用同一套动作）。
    /// </summary>
    Action = 0,

    /// <summary>
    /// 给智能体派一个任务，可等它完成后把结果交给后续步骤。
    /// </summary>
    AgentTask = 1,

    /// <summary>
    /// 让模型处理一段文本：总结、分类、提取、改写。
    /// </summary>
    Llm = 2,

    /// <summary>
    /// 等待一段时间再继续。
    /// </summary>
    Delay = 3,

    /// <summary>
    /// 条件分支：按顺序判断各分支的条件，执行第一个成立的分支里的步骤。
    /// </summary>
    Branch = 4,

    /// <summary>
    /// 遍历列表：对列表里的每一项依次执行一组步骤，步骤里用 item、index 引用当前项。
    /// </summary>
    ForEach = 5,

    /// <summary>
    /// 调用外部 HTTP 接口，例如通知其他系统或查询外部数据。
    /// </summary>
    Http = 6,
}

/// <summary>
/// 条件分支里的一条分支。
/// </summary>
public sealed record WorkflowBranch
{
    public required string Name { get; init; }

    /// <summary>
    /// 分支条件表达式；留空表示「其他情况」，只能放在最后。
    /// </summary>
    public string? When { get; init; }

    public List<WorkflowStep> Steps { get; init; } = [];
}

/// <summary>
/// 工作流的一个步骤。按顺序执行；字段按步骤类型取用，文本字段和动作输入里可以写 {{ 表达式 }} 引用触发数据和前面步骤的输出。
/// </summary>
public sealed record WorkflowStep
{
    /// <summary>
    /// 步骤标识（小写字母开头，字母数字下划线），后续步骤用 steps.&lt;key&gt;.output 引用它的输出。
    /// </summary>
    public required string Key { get; init; }

    public required string Name { get; init; }

    public WorkflowStepKind Kind { get; init; }

    /// <summary>
    /// 执行条件表达式，为假时跳过本步骤；留空表示总是执行。
    /// </summary>
    public string? When { get; init; }

    /// <summary>
    /// 本步骤失败后是否继续执行后续步骤。
    /// </summary>
    public bool ContinueOnError { get; init; }

    /// <summary>
    /// Action：动作键，例如 tasks.create。
    /// </summary>
    public string? ActionKey { get; init; }

    /// <summary>
    /// Action：动作输入（JSON 对象，字符串值里可以写模板）。
    /// </summary>
    public JsonElement? Input { get; init; }

    /// <summary>
    /// AgentTask：负责的智能体。
    /// </summary>
    public Guid? AgentId { get; init; }

    /// <summary>
    /// AgentTask：任务标题（模板）。
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// AgentTask：任务说明（模板）。
    /// </summary>
    public string? Instructions { get; init; }

    /// <summary>
    /// AgentTask：是否等任务完成（或取消）后再继续。
    /// </summary>
    public bool WaitForCompletion { get; init; }

    /// <summary>
    /// AgentTask：最长等待分钟数，超时本步骤失败；留空表示一直等。
    /// </summary>
    public int? TimeoutMinutes { get; init; }

    /// <summary>
    /// Llm：提示词（模板）。
    /// </summary>
    public string? Prompt { get; init; }

    /// <summary>
    /// Delay：等待秒数。
    /// </summary>
    public int? DelaySeconds { get; init; }

    /// <summary>
    /// Branch：各分支，按顺序判断。
    /// </summary>
    public List<WorkflowBranch>? Branches { get; init; }

    /// <summary>
    /// ForEach：结果为列表的表达式，例如 steps.find.output.items。
    /// </summary>
    public string? Items { get; init; }

    /// <summary>
    /// ForEach：对每一项执行的步骤。
    /// </summary>
    public List<WorkflowStep>? Steps { get; init; }

    /// <summary>
    /// Http：请求方法（GET、POST、PUT、PATCH、DELETE）。
    /// </summary>
    public string? Method { get; init; }

    /// <summary>
    /// Http：请求地址（模板）。
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Http：请求头（值可以写模板）。
    /// </summary>
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Http：请求体。对象和数组按 JSON 发送，单个字符串按纯文本发送；字符串值里可以写模板。GET 和 DELETE 不能带请求体。
    /// </summary>
    public JsonElement? Body { get; init; }

    /// <summary>
    /// Http：超时秒数，默认 30。
    /// </summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>
    /// 本步骤直接包含的子步骤（分支里的或遍历的）。
    /// </summary>
    public IEnumerable<WorkflowStep> Children()
        => Kind switch
        {
            WorkflowStepKind.Branch => Branches?.SelectMany(b => b.Steps) ?? [],
            WorkflowStepKind.ForEach => Steps ?? [],
            _ => [],
        };

    /// <summary>
    /// 本步骤及其所有后代，按定义顺序。
    /// </summary>
    public IEnumerable<WorkflowStep> SelfAndDescendants()
        => Children().SelectMany(c => c.SelfAndDescendants()).Prepend(this);
}

/// <summary>
/// 自动化工作流：一个触发器加一串按顺序执行的步骤。
/// 以最后保存定义的成员（或智能体）的身份和权限运行：谁改了步骤，就由谁的权限兜底，编辑工作流不能借用他人权限。
/// </summary>
public sealed class Workflow : TenantEntity
{
    public required string Name { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 停用后不再被事件和定时触发，仍可手动运行（便于试跑）。
    /// </summary>
    public bool IsEnabled { get; set; }

    public WorkflowTriggerKind TriggerKind { get; set; }

    /// <summary>
    /// 事件触发：事件类型，例如 tasks.created。
    /// </summary>
    public string? EventType { get; set; }

    /// <summary>
    /// 事件触发：过滤条件表达式，为假时不触发。
    /// </summary>
    public string? EventFilter { get; set; }

    /// <summary>
    /// 定时触发：5 段 cron 表达式（分 时 日 月 周）。
    /// </summary>
    public string? Cron { get; set; }

    /// <summary>
    /// 定时触发：cron 所在时区（IANA 名称，例如 Asia/Shanghai）。
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>
    /// 定时触发的下一次执行时间；停用或非定时触发时为空。
    /// </summary>
    public DateTimeOffset? NextRunAt { get; set; }

    public List<WorkflowStep> Steps { get; set; } = [];

    /// <summary>
    /// 运行身份：最后一次保存定义的成员或智能体。
    /// </summary>
    public Actor Owner { get; set; }

    /// <summary>
    /// 定义版本，每次保存加一；运行记录保存当时的版本和步骤快照。
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Webhook 触发：地址里的随机令牌，持有地址即可触发，可以重新生成让旧地址失效。
    /// </summary>
    public string? WebhookToken { get; set; }
}
