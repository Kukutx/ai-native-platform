using System.Text.Json;

namespace AiNative.Application.Workflows;

/// <summary>
/// HTTP 请求步骤用的出站客户端。实现负责超时、响应大小上限，以及默认禁止访问内网地址（防止借工作流探测内网）。
/// </summary>
public interface IWorkflowHttpClient
{
    /// <exception cref="WorkflowHttpException">请求没有拿到响应（地址不允许、连接失败、超时、响应过大）。</exception>
    Task<WorkflowHttpResponse> SendAsync(WorkflowHttpRequest request, CancellationToken cancellationToken);
}

/// <param name="Body">JSON 字符串按纯文本发送，其他 JSON 值按 application/json 发送；请求头里写了 Content-Type 时以请求头为准。</param>
public sealed record WorkflowHttpRequest(string Method, Uri Url, IReadOnlyDictionary<string, string> Headers, JsonElement? Body, TimeSpan Timeout);

public sealed record WorkflowHttpResponse(int Status, string? ContentType, string Body)
{
    public bool IsSuccess => Status is >= 200 and < 300;
}

public sealed class WorkflowHttpException(string message) : Exception(message);

/// <summary>
/// 运行步骤的位置。顶层步骤的路径是它的序号（"2"）；分支和遍历里的步骤在父路径后接分支序号或第几项、再接子步骤序号（"2.1.0"）。
/// </summary>
internal readonly record struct StepPosition(int Top, string Path)
{
    public static StepPosition ForTop(int index) => new(index, index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public bool IsTop => !Path.Contains('.', StringComparison.Ordinal);

    public StepPosition Child(int group, int index) => new(Top, $"{Path}.{group}.{index}");
}

/// <summary>
/// 按数字逐段比较步骤路径（"2.10.0" 排在 "2.9.0" 之后），得到执行顺序。
/// </summary>
internal sealed class StepPathComparer : IComparer<string>
{
    public static readonly StepPathComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var left = (x ?? string.Empty).Split('.');
        var right = (y ?? string.Empty).Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var order = int.TryParse(left[i], out var l) && int.TryParse(right[i], out var r)
                ? l.CompareTo(r)
                : string.CompareOrdinal(left[i], right[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return left.Length.CompareTo(right.Length);
    }
}
