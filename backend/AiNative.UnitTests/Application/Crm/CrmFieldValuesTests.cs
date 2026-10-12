using System.Text.Json;
using AiNative.Application.Crm;
using AiNative.Domain.Crm;

namespace AiNative.UnitTests.Application.Crm;

public sealed class CrmFieldValuesTests
{
    [Theory]
    [InlineData("\"  华东大区 \"", "\"华东大区\"")]
    [InlineData("1200", "\"1200\"")]
    [InlineData("\"\"", null)]
    [InlineData("null", null)]
    public void Text_is_trimmed_and_empty_is_dropped(string input, string? expected)
        => Normalize(Field(CrmFieldType.Text), input).Should().Be(expected);

    [Theory]
    [InlineData("1200", "1200")]
    [InlineData("\"1,200.50\"", "1200.50")]
    [InlineData("\"1，200\"", "1200")]
    [InlineData("\" -3 \"", "-3")]
    [InlineData("\"\"", null)]
    public void Numbers_accept_json_numbers_and_formatted_text(string input, string? expected)
        => Normalize(Field(CrmFieldType.Number), input).Should().Be(expected);

    [Theory]
    [InlineData("\"2026-10-12\"")]
    [InlineData("\"2026/10/12\"")]
    [InlineData("\"2026.10.12\"")]
    [InlineData("\"20261012\"")]
    [InlineData("\"2026年10月12日\"")]
    [InlineData("\"2026-10-12T23:30:00+08:00\"")]
    [InlineData("\"2026-10-12 08:00\"")]
    public void Dates_are_stored_as_iso_dates(string input)
        => Normalize(Field(CrmFieldType.Date), input).Should().Be("\"2026-10-12\"");

    [Fact]
    public void Single_dates_digits_are_padded()
        => Normalize(Field(CrmFieldType.Date), "\"2026/3/5\"").Should().Be("\"2026-03-05\"");

    [Fact]
    public void Select_matches_options_ignoring_case_and_keeps_the_option_text()
    {
        var field = Field(CrmFieldType.Select, "VIP", "普通");

        Normalize(field, "\" vip \"").Should().Be("\"VIP\"");
        Error(field, "\"黄金\"").Should().Be("字段没有「黄金」这个选项。");
        Error(field, "[\"VIP\"]").Should().Be("字段只能选一个选项。");
    }

    [Fact]
    public void Multi_select_accepts_arrays_or_separated_text_and_removes_duplicates()
    {
        var field = Field(CrmFieldType.MultiSelect, "线上", "线下", "渠道");

        Normalize(field, "[\"线下\", \"线上\", \"线下\"]").Should().Be("[\"线下\",\"线上\"]");
        Normalize(field, "\"线上、渠道；线下\"").Should().Be("[\"线上\",\"渠道\",\"线下\"]");
        Normalize(field, "[]").Should().BeNull();
        Error(field, "\"线上,展会\"").Should().Be("字段没有「展会」这个选项。");
    }

    [Theory]
    [InlineData("true", "true")]
    [InlineData("\"是\"", "true")]
    [InlineData("\"√\"", "true")]
    [InlineData("1", "true")]
    [InlineData("false", null)]
    [InlineData("\"否\"", null)]
    [InlineData("\"\"", null)]
    public void Checkbox_only_stores_true(string input, string? expected)
        => Normalize(Field(CrmFieldType.Checkbox), input).Should().Be(expected);

    [Theory]
    [InlineData(CrmFieldType.Number, "\"十二\"", "字段需要填写数字。")]
    [InlineData(CrmFieldType.Date, "\"下周一\"", "字段需要填写日期，例如 2026-10-12。")]
    [InlineData(CrmFieldType.Date, "\"2026-02-30\"", "字段需要填写日期，例如 2026-10-12。")]
    [InlineData(CrmFieldType.Checkbox, "\"也许\"", "字段需要填写“是”或“否”。")]
    [InlineData(CrmFieldType.Text, "{\"a\": 1}", "字段需要填写文字。")]
    public void Invalid_values_are_explained(CrmFieldType type, string input, string message)
        => Error(Field(type), input).Should().Be(message);

    [Fact]
    public void Text_longer_than_the_limit_is_rejected()
        => Error(Field(CrmFieldType.Text), JsonSerializer.Serialize(new string('字', CrmFieldLimits.MaxTextLength + 1))).Should().Contain("最多");

    [Fact]
    public void Merge_applies_changes_checks_required_and_drops_values_of_deleted_fields()
    {
        var region = Field(CrmFieldType.Select, "华东", "华南");
        region.Key = "region";
        region.Label = "区域";
        region.Required = true;
        var size = Field(CrmFieldType.Number);
        size.Key = "size";
        size.Label = "规模";
        var current = new Dictionary<string, JsonElement>
        {
            ["region"] = Json("\"华东\""),
            ["size"] = Json("10"),
            ["removed"] = Json("\"旧字段的值\""),
        };

        var errors = new Dictionary<string, string>();
        var merged = CrmFieldValues.Merge([region, size], current, new Dictionary<string, JsonElement> { ["size"] = Json("null") }, errors);
        errors.Should().BeEmpty();
        merged.Keys.Should().BeEquivalentTo(["region"]);

        merged = CrmFieldValues.Merge([region, size], null, new Dictionary<string, JsonElement> { ["size"] = Json("\"多\""), ["nope"] = Json("1") }, errors);
        errors.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["size"] = "规模需要填写数字。",
            ["nope"] = "没有标识为「nope」的自定义字段。",
            ["region"] = "请填写区域。",
        });
        merged.Should().BeEmpty();
    }

    private static CrmFieldDefinition Field(CrmFieldType type, params string[] options) => new()
    {
        Key = "field",
        Label = "字段",
        Type = type,
        Options = [.. options],
    };

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string? Normalize(CrmFieldDefinition field, string json)
    {
        CrmFieldValues.TryNormalize(field, Json(json), out var normalized, out var error).Should().BeTrue(error);
        return normalized?.GetRawText();
    }

    private static string Error(CrmFieldDefinition field, string json)
    {
        CrmFieldValues.TryNormalize(field, Json(json), out _, out var error).Should().BeFalse();
        return error!;
    }
}
