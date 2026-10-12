using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiNative.Application.Common;

namespace AiNative.Application.Workflows.Expressions;

/// <summary>
/// 文本模板：字符串里的 {{ 表达式 }} 替换成求值结果。
/// 整个字符串只有一个 {{ }} 时保留值的原始类型（数字、对象、数组原样传给动作）；和其他文字混排时转成文本。
/// </summary>
public static class WorkflowTemplate
{
    private const string Open = "{{";
    private const string Close = "}}";

    /// <summary>
    /// 校验模板里所有表达式，返回第一个错误；没有问题返回 null。
    /// </summary>
    public static string? Validate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            foreach (var segment in Split(text))
            {
                if (segment.IsExpression)
                {
                    WorkflowExpression.Parse(segment.Text);
                }
            }

            return null;
        }
        catch (WorkflowExpressionException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// 递归校验 JSON 里所有字符串值（以及属性名不参与模板）。
    /// </summary>
    public static string? Validate(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Validate(value.GetString()),
        JsonValueKind.Object => value.EnumerateObject().Select(p => Validate(p.Value)).FirstOrDefault(e => e is not null),
        JsonValueKind.Array => value.EnumerateArray().Select(Validate).FirstOrDefault(e => e is not null),
        _ => null,
    };

    /// <summary>
    /// 模板里是否有表达式引用了这个顶层变量。模板本身不合法时返回 false（语法错误另行报告）。
    /// </summary>
    public static bool UsesRoot(string? text, string root)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains(Open, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return Split(text).Any(s => s.IsExpression && WorkflowExpression.Parse(s.Text).Roots.Contains(root));
        }
        catch (WorkflowExpressionException)
        {
            return false;
        }
    }

    public static bool UsesRoot(JsonElement value, string root) => value.ValueKind switch
    {
        JsonValueKind.String => UsesRoot(value.GetString(), root),
        JsonValueKind.Object => value.EnumerateObject().Any(p => UsesRoot(p.Value, root)),
        JsonValueKind.Array => value.EnumerateArray().Any(v => UsesRoot(v, root)),
        _ => false,
    };

    public static string RenderString(string? text, JsonElement context)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains(Open, StringComparison.Ordinal))
        {
            return text ?? string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var segment in Split(text))
        {
            builder.Append(segment.IsExpression ? Values.ToText(WorkflowExpression.Parse(segment.Text).Evaluate(context)) : segment.Text);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 渲染 JSON：字符串值按模板求值，对象和数组逐项递归，其余原样保留。
    /// </summary>
    public static JsonElement Render(JsonElement value, JsonElement context)
        => Values.ToElement(RenderNode(value, context));

    private static object? RenderNode(JsonElement value, JsonElement context)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString()!;
                if (!text.Contains(Open, StringComparison.Ordinal))
                {
                    return value;
                }

                if (Split(text) is [{ IsExpression: true } only])
                {
                    return WorkflowExpression.Parse(only.Text).Evaluate(context);
                }

                return RenderString(text, context);
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in value.EnumerateObject())
                {
                    obj[property.Name] = JsonSerializer.SerializeToNode(Values.ToElement(RenderNode(property.Value, context)), PlatformJson.Options);
                }

                return PlatformJson.ToElement(obj);
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in value.EnumerateArray())
                {
                    array.Add(JsonSerializer.SerializeToNode(Values.ToElement(RenderNode(item, context)), PlatformJson.Options));
                }

                return PlatformJson.ToElement(array);
            default:
                return value;
        }
    }

    private static List<Segment> Split(string text)
    {
        var segments = new List<Segment>();
        var position = 0;
        while (position < text.Length)
        {
            var start = text.IndexOf(Open, position, StringComparison.Ordinal);
            if (start < 0)
            {
                segments.Add(new Segment(text[position..], false));
                break;
            }

            var end = text.IndexOf(Close, start + Open.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new WorkflowExpressionException($"模板中第 {start + 1} 个字符开始的 {{{{ 没有对应的 }}}}。");
            }

            if (start > position)
            {
                segments.Add(new Segment(text[position..start], false));
            }

            var expression = text[(start + Open.Length)..end].Trim();
            if (expression.Length == 0)
            {
                throw new WorkflowExpressionException($"模板中第 {start + 1} 个字符处的 {{{{ }}}} 是空的。");
            }

            segments.Add(new Segment(expression, true));
            position = end + Close.Length;
        }

        return segments;
    }

    private readonly record struct Segment(string Text, bool IsExpression);
}
