using System.Text.Json;
using AiNative.Application.Workflows.Expressions;

namespace AiNative.UnitTests.Application.Workflows;

public sealed class WorkflowExpressionTests
{
    private static readonly JsonElement Context = JsonDocument.Parse("""
        {
          "trigger": {
            "task": { "title": "修复登录问题", "priority": "urgent", "number": 12, "tags": ["bug", "客户"] },
            "from": "todo",
            "to": "done",
            "actor": { "kind": "user", "name": "小王" }
          },
          "input": { "客户": "星火科技", "amount": 1500, "empty": "" },
          "steps": { "review": { "status": "succeeded", "output": { "items": [ { "title": "第一项" } ], "text": "结论：紧急" } } }
        }
        """).RootElement;

    [Theory]
    [InlineData("trigger.to == 'done'", true)]
    [InlineData("trigger.to != 'done'", false)]
    [InlineData("trigger.from == 'todo' && trigger.to == 'done'", true)]
    [InlineData("trigger.from == 'backlog' || trigger.to == 'done'", true)]
    [InlineData("!(trigger.to == 'done')", false)]
    [InlineData("input.amount > 1000", true)]
    [InlineData("input.amount >= 1500 && input.amount < 2000", true)]
    [InlineData("trigger.task.number <= 11", false)]
    [InlineData("trigger.task.title contains '登录'", true)]
    [InlineData("trigger.task.tags contains 'bug'", true)]
    [InlineData("steps.review.output.text contains '紧急'", true)]
    [InlineData("input.客户 == '星火科技'", true)]
    [InlineData("input['客户'] == \"星火科技\"", true)]
    [InlineData("steps.review.output.items[0].title == '第一项'", true)]
    [InlineData("steps.missing.output", false)]
    [InlineData("input.empty", false)]
    [InlineData("length(trigger.task.tags) == 2", true)]
    [InlineData("lower('ABC') == 'abc'", true)]
    [InlineData("default(input.empty, '无') == '无'", true)]
    [InlineData("trigger.actor.kind == 'user'", true)]
    [InlineData("input.amount == '1500'", false)]
    public void Evaluates_conditions(string expression, bool expected)
        => WorkflowExpression.Parse(expression).IsTrue(Context).Should().Be(expected);

    [Fact]
    public void Missing_paths_evaluate_to_null_instead_of_failing()
        => WorkflowExpression.Parse("trigger.nothing.deeper[3].x").Evaluate(Context).Should().BeNull();

    [Theory]
    [InlineData("trigger.to ==")]
    [InlineData("(trigger.to == 'done'")]
    [InlineData("'unterminated")]
    [InlineData("trigger.to = 'done'")]
    [InlineData("unknown(1)")]
    [InlineData("length(1, 2)")]
    [InlineData("trigger.")]
    public void Rejects_invalid_syntax_with_a_message(string expression)
        => WorkflowExpression.Validate(expression).Should().NotBeNullOrWhiteSpace();
}

public sealed class WorkflowTemplateTests
{
    private static readonly JsonElement Context = JsonDocument.Parse("""
        { "trigger": { "task": { "key": "T-7", "title": "写周报", "assigneeAgentId": null, "number": 7 } },
          "steps": { "draft": { "output": { "text": "本周完成 3 项", "json": { "count": 3 } } } } }
        """).RootElement;

    [Fact]
    public void Interpolates_text()
        => WorkflowTemplate.RenderString("任务 {{ trigger.task.key }}《{{trigger.task.title}}》：{{ steps.draft.output.text }}", Context)
            .Should().Be("任务 T-7《写周报》：本周完成 3 项");

    [Fact]
    public void Single_expression_keeps_the_original_type()
    {
        var template = JsonDocument.Parse("""{ "count": "{{ steps.draft.output.json.count }}", "data": "{{ steps.draft.output.json }}", "label": "共 {{ steps.draft.output.json.count }} 项", "nested": ["{{ trigger.task.number }}"] }""").RootElement;

        var rendered = WorkflowTemplate.Render(template, Context);

        rendered.GetProperty("count").GetInt32().Should().Be(3);
        rendered.GetProperty("data").GetProperty("count").GetInt32().Should().Be(3);
        rendered.GetProperty("label").GetString().Should().Be("共 3 项");
        rendered.GetProperty("nested")[0].GetInt32().Should().Be(7);
    }

    [Fact]
    public void Missing_values_render_as_empty_text_or_null()
    {
        WorkflowTemplate.RenderString("[{{ trigger.task.missing }}]", Context).Should().Be("[]");
        var rendered = WorkflowTemplate.Render(JsonDocument.Parse("""{ "agent": "{{ trigger.task.assigneeAgentId }}" }""").RootElement, Context);
        rendered.GetProperty("agent").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("{{ trigger.to == }}")]
    [InlineData("没有结束 {{ trigger.to")]
    [InlineData("空的 {{ }}")]
    public void Validation_reports_broken_templates(string text)
        => WorkflowTemplate.Validate(text).Should().NotBeNull();

    [Fact]
    public void Plain_text_needs_no_rendering()
    {
        WorkflowTemplate.Validate("普通文字").Should().BeNull();
        WorkflowTemplate.RenderString("普通文字", Context).Should().Be("普通文字");
    }
}
