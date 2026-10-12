using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Actions;
using AiNative.Contracts.Common;
using AiNative.Contracts.Crm;
using AiNative.Contracts.Platform;
using AiNative.Domain.Crm;
using AiNative.IntegrationTests.Infrastructure;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.EntityFrameworkCore;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace AiNative.IntegrationTests;

public sealed class CrmCustomFieldTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Custom_fields_are_validated_stored_and_cleaned_up_with_their_definitions()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var region = await PostAsync<CrmFieldDto>(client, "/api/crm/fields", Field("region", "区域", CrmFieldType.Select, required: true, options: ["华东", "华南", "华北"]));
        var employees = await PostAsync<CrmFieldDto>(client, "/api/crm/fields", Field("employees", "员工数", CrmFieldType.Number));
        await PostAsync<CrmFieldDto>(client, "/api/crm/fields", Field("founded", "成立日期", CrmFieldType.Date));
        await PostAsync<CrmFieldDto>(client, "/api/crm/fields", Field("vip", "重点客户", CrmFieldType.Checkbox));
        var channels = await PostAsync<CrmFieldDto>(client, "/api/crm/fields", Field("channels", "渠道", CrmFieldType.MultiSelect, options: ["线上", "线下", "代理"]));
        await PostAsync<CrmFieldDto>(client, "/api/crm/fields", Field("wechat", "微信", CrmFieldType.Text, recordType: CrmRecordType.Contact));

        region.Position.Should().Be(0);
        channels.Position.Should().Be(4);
        (await Problem(client.PostAsJsonAsync("/api/crm/fields", Field("region", "地区", CrmFieldType.Text), PlatformJson.Options, Ct))).Should().Be((409, "crm_field.key_taken"));
        (await Problem(client.PostAsJsonAsync("/api/crm/fields", Field("area", "区域", CrmFieldType.Text), PlatformJson.Options, Ct))).Should().Be((409, "crm_field.label_taken"));
        (await Problem(client.PostAsJsonAsync("/api/crm/fields", Field("Area", "地区", CrmFieldType.Text), PlatformJson.Options, Ct))).Should().Be((400, "validation_failed"));
        (await Problem(client.PostAsJsonAsync("/api/crm/fields", Field("flag", "标记", CrmFieldType.Checkbox, required: true), PlatformJson.Options, Ct))).Should().Be((400, "validation_failed"));
        (await Problem(client.PostAsJsonAsync("/api/crm/fields", Field("level", "等级", CrmFieldType.Select), PlatformJson.Options, Ct))).Should().Be((400, "validation_failed"));
        // 同一个标识可以在另一类记录上使用。
        await PostAsync<CrmFieldDto>(client, "/api/crm/fields", Field("region", "区域", CrmFieldType.Text, recordType: CrmRecordType.Deal));

        // 必填字段没填、值不合法、字段不存在都按字段报错。
        var missing = await client.PostAsJsonAsync("/api/crm/companies", new SaveCrmCompanyRequest { Name = "星火科技" }, PlatformJson.Options, Ct);
        (await ErrorsAsync(missing)).Should().ContainKey("customFields.region");
        var invalid = await client.PostAsJsonAsync("/api/crm/companies", new SaveCrmCompanyRequest
        {
            Name = "星火科技",
            CustomFields = Values(new { region = "西北", employees = "很多", unknown = 1 }),
        }, PlatformJson.Options, Ct);
        (await ErrorsAsync(invalid)).Keys.Should().BeEquivalentTo(["customFields.region", "customFields.employees", "customFields.unknown"]);

        var company = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest
        {
            Name = "星火科技",
            CustomFields = Values(new { region = "华东", employees = "1,200", founded = "2020/3/5", vip = "是", channels = "线上、线下" }),
        });
        Raw(company.CustomFields).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["region"] = "\"华东\"",
            ["employees"] = "1200",
            ["founded"] = "\"2020-03-05\"",
            ["vip"] = "true",
            ["channels"] = "[\"线上\",\"线下\"]",
        });

        // 编辑时不传自定义字段表示不改；传了就整体替换。
        var renamed = await PutAsync<CrmCompanyDto>(client, $"/api/crm/companies/{company.Id}", new SaveCrmCompanyRequest { Name = "星火科技集团" });
        renamed.CustomFields.Should().HaveCount(5);
        var replaced = await PutAsync<CrmCompanyDto>(client, $"/api/crm/companies/{company.Id}", new SaveCrmCompanyRequest
        {
            Name = "星火科技集团",
            CustomFields = Values(new { region = "华南", channels = new[] { "线上", "线下" }, vip = false }),
        });
        Raw(replaced.CustomFields).Should().BeEquivalentTo(new Dictionary<string, string> { ["region"] = "\"华南\"", ["channels"] = "[\"线上\",\"线下\"]" });

        // 删除选项会从记录里清掉；只改大小写或名称、说明不影响已有的值。
        await PutAsync<CrmFieldDto>(client, $"/api/crm/fields/{channels.Id}", Field("channels", "销售渠道", CrmFieldType.MultiSelect, options: ["线上", "代理"]));
        await PutAsync<CrmFieldDto>(client, $"/api/crm/fields/{region.Id}", Field("region", "区域", CrmFieldType.Select, required: true, options: ["华东", "华北"]));
        var detail = await GetAsync<CrmCompanyDetailDto>(client, $"/api/crm/companies/{company.Id}");
        Raw(detail.Company.CustomFields).Should().BeEquivalentTo(new Dictionary<string, string> { ["channels"] = "[\"线上\"]" });
        detail.Company.UpdatedAt.Should().BeCloseTo(replaced.UpdatedAt, TimeSpan.FromMilliseconds(1));

        // 标识、类型创建后不能改。
        (await Problem(client.PutAsJsonAsync($"/api/crm/fields/{employees.Id}", Field("staff", "员工数", CrmFieldType.Number), PlatformJson.Options, Ct))).Should().Be((400, "validation_failed"));
        (await Problem(client.PutAsJsonAsync($"/api/crm/fields/{employees.Id}", Field("employees", "员工数", CrmFieldType.Text), PlatformJson.Options, Ct))).Should().Be((400, "validation_failed"));

        // 删除字段时清除所有记录里的值。
        await PutAsync<CrmCompanyDto>(client, $"/api/crm/companies/{company.Id}", new SaveCrmCompanyRequest
        {
            Name = "星火科技集团",
            CustomFields = Values(new { region = "华北", employees = 30, channels = "代理" }),
        });
        (await client.DeleteAsync(new Uri($"/api/crm/fields/{employees.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var stored = await WithDbAsync(db => db.CrmCompanies.IgnoreQueryFilters().SingleAsync(c => c.Id == company.Id, Ct));
        stored.CustomFields.Keys.Should().BeEquivalentTo(["region", "channels"]);

        var fields = await GetAsync<List<CrmFieldDto>>(client, "/api/crm/fields?recordType=company");
        var reordered = await PutAsync<List<CrmFieldDto>>(client, "/api/crm/fields/order", new ReorderCrmFieldsRequest
        {
            RecordType = CrmRecordType.Company,
            Ids = [.. fields.Select(f => f.Id).Reverse()],
        });
        reordered.Select(f => f.Key).Should().Equal("channels", "vip", "founded", "region");
        (await GetAsync<List<CrmFieldDto>>(client, "/api/crm/fields?recordType=company")).Select(f => f.Key).Should().Equal("channels", "vip", "founded", "region");
        (await GetAsync<List<CrmFieldDto>>(client, "/api/crm/fields")).Should().HaveCount(6);
        (await Problem(client.PutAsJsonAsync("/api/crm/fields/order", new ReorderCrmFieldsRequest { RecordType = CrmRecordType.Company, Ids = [fields[0].Id] }, PlatformJson.Options, Ct)))
            .Should().Be((400, "validation_failed"));
    }

    [Fact]
    public async Task Field_settings_need_the_configure_permission_and_stay_inside_the_tenant()
    {
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        var roles = await GetAsync<List<RoleDto>>(ownerClient, "/api/roles");
        using var viewer = Client(await AddMemberAsync(ownerClient, "viewer@example.com", roles.Single(r => r.Key == "viewer").Id));
        using var member = Client(await AddMemberAsync(ownerClient, "member@example.com", roles.Single(r => r.Key == "member").Id));
        var field = await PostAsync<CrmFieldDto>(ownerClient, "/api/crm/fields", Field("region", "区域", CrmFieldType.Text));

        (await GetAsync<List<CrmFieldDto>>(viewer, "/api/crm/fields")).Should().ContainSingle();
        (await viewer.PostAsJsonAsync("/api/crm/fields", Field("level", "等级", CrmFieldType.Text), PlatformJson.Options, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.DeleteAsync(new Uri($"/api/crm/fields/{field.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Import(viewer, "客户.csv", "名称\n星火\n", CrmRecordType.Company, ["name"])).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var other = await RegisterAsync("other@example.com", "别的空间");
        using var otherClient = Client(other.AccessToken);
        (await GetAsync<List<CrmFieldDto>>(otherClient, "/api/crm/fields")).Should().BeEmpty();
        (await otherClient.PutAsJsonAsync($"/api/crm/fields/{field.Id}", Field("region", "地区", CrmFieldType.Text), PlatformJson.Options, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await otherClient.DeleteAsync(new Uri($"/api/crm/fields/{field.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 另一个空间的同名字段互不影响，那边的记录也不认这边的字段。
        await PostAsync<CrmFieldDto>(otherClient, "/api/crm/fields", Field("region", "区域", CrmFieldType.Number));
        var rejected = await otherClient.PostAsJsonAsync("/api/crm/companies", new SaveCrmCompanyRequest { Name = "x", CustomFields = Values(new { region = "华东" }) }, PlatformJson.Options, Ct);
        (await ErrorsAsync(rejected)).Should().ContainKey("customFields.region");
    }

    [Fact]
    public async Task Actions_read_definitions_and_merge_custom_field_changes()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        await PostAsync<CrmFieldDto>(client, "/api/crm/fields", Field("wechat", "微信", CrmFieldType.Text, recordType: CrmRecordType.Contact));
        await PostAsync<CrmFieldDto>(client, "/api/crm/fields", Field("level", "级别", CrmFieldType.Select, recordType: CrmRecordType.Contact, options: ["决策人", "使用者"]));
        await PostAsync<CrmFieldDto>(client, "/api/crm/fields", Field("source", "来源", CrmFieldType.Select, recordType: CrmRecordType.Deal, required: true, options: ["官网", "展会"]));

        var fields = await InvokeAsync(client, "crm.list_fields", new { recordType = "contact" });
        fields.EnumerateArray().Select(f => f.GetProperty("key").GetString()).Should().Equal("wechat", "level");
        fields[1].GetProperty("type").GetString().Should().Be("select");

        var contact = await InvokeAsync(client, "crm.create_contact", new { name = "王经理", customFields = new { wechat = "wx-wang", level = "决策人" } });
        var contactId = contact.GetProperty("id").GetGuid();
        contact.GetProperty("customFields").GetProperty("level").GetString().Should().Be("决策人");

        // 只改传了的字段；null 表示清空。
        var updated = await InvokeAsync(client, "crm.update_contact", new { contactId, customFields = new Dictionary<string, object?> { ["wechat"] = null } });
        updated.GetProperty("customFields").TryGetProperty("wechat", out _).Should().BeFalse();
        updated.GetProperty("customFields").GetProperty("level").GetString().Should().Be("决策人");
        var untouched = await InvokeAsync(client, "crm.update_contact", new { contactId, title = "总监" });
        untouched.GetProperty("customFields").GetProperty("level").GetString().Should().Be("决策人");

        var missing = await PostAsync<InvokeActionResponse>(client, "/api/actions/crm.create_deal/invoke", new { title = "年度采购" });
        missing.Status.Should().Be("invalid_input");
        missing.Error.Should().Contain("来源");
        var deal = await InvokeAsync(client, "crm.create_deal", new { title = "年度采购", customFields = new { source = "展会" } });
        deal.GetProperty("customFields").GetProperty("source").GetString().Should().Be("展会");
        var wrongOption = await PostAsync<InvokeActionResponse>(client, "/api/actions/crm.update_deal/invoke", new { dealId = deal.GetProperty("id").GetGuid(), customFields = new { source = "电话" } });
        wrongOption.Status.Should().Be("invalid_input");
        wrongOption.Error.Should().Contain("没有「电话」这个选项");
    }

    [Fact]
    public async Task Company_import_previews_checks_and_then_creates_or_updates()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        await PostAsync<CrmFieldDto>(client, "/api/crm/fields", Field("region", "区域", CrmFieldType.Select, options: ["华东", "华南"]));
        await PostAsync<CrmFieldDto>(client, "/api/crm/fields", Field("employees", "员工数", CrmFieldType.Number));
        var existing = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "云帆物流", Industry = "运输", Tags = ["老客户"] });
        const string csv = """
            公司名称,行业,电话,标签,负责人,区域,员工数,无关列
            星火科技,软件,010-1234,"重点,华东",owner@example.com,华东,"1,200",x
            云帆物流,物流,,新签,,华南,300,
            星火科技,重复的行,,,,,,
            ,缺少名称,,,,,,
            蓝海贸易,贸易,,,不存在的人,西北,很多,
            """;

        var preview = await PreviewAsync(client, "客户.csv", csv, CrmRecordType.Company);
        preview.RowCount.Should().Be(5);
        preview.Columns.Select(c => c.SuggestedTarget).Should().Equal("name", "industry", "phone", "tags", "owner", "custom.region", "custom.employees", null);
        preview.Columns[0].Samples.Should().Equal("星火科技", "云帆物流", "星火科技");
        preview.Targets.Should().Contain(t => t.Key == "custom.region" && t.Hint == "可选：华东、华南");
        string?[] mapping = ["name", "industry", "phone", "tags", "owner", "custom.region", "custom.employees", ""];

        var check = await ImportAsync(client, "客户.csv", csv, CrmRecordType.Company, mapping, dryRun: true);
        check.Should().BeEquivalentTo(new { DryRun = true, Total = 5, Created = 1, Updated = 0, Skipped = 1, Failed = 3 });
        check.Errors.Select(e => e.Row).Should().Equal(4, 5, 6);
        check.Errors[0].Message.Should().Contain("第 2 行");
        check.Errors[2].Message.Should().Contain("找不到成员「不存在的人」").And.Contain("没有「西北」这个选项").And.Contain("员工数需要填写数字");
        (await GetAsync<PagedResult<CrmCompanyDto>>(client, "/api/crm/companies")).Total.Should().Be(1);

        var result = await ImportAsync(client, "客户.csv", csv, CrmRecordType.Company, mapping, duplicates: CrmImportDuplicateMode.Update);
        result.Should().BeEquivalentTo(new { DryRun = false, Total = 5, Created = 1, Updated = 1, Skipped = 0, Failed = 3 });
        var companies = (await GetAsync<PagedResult<CrmCompanyDto>>(client, "/api/crm/companies")).Items.ToDictionary(c => c.Name);
        companies.Keys.Should().BeEquivalentTo(["星火科技", "云帆物流"]);
        var created = companies["星火科技"];
        created.Industry.Should().Be("软件");
        created.Tags.Should().Equal("重点", "华东");
        created.Owner!.Id.Should().Be(auth.Session.User.Id);
        Raw(created.CustomFields).Should().BeEquivalentTo(new Dictionary<string, string> { ["region"] = "\"华东\"", ["employees"] = "1200" });
        var updated = companies["云帆物流"];
        updated.Id.Should().Be(existing.Id);
        updated.Industry.Should().Be("物流");
        updated.Tags.Should().Equal("老客户", "新签");
        Raw(updated.CustomFields).Should().BeEquivalentTo(new Dictionary<string, string> { ["region"] = "\"华南\"", ["employees"] = "300" });

        // 再导一次：已有的跳过，不会重复创建。
        var again = await ImportAsync(client, "客户.csv", csv, CrmRecordType.Company, mapping);
        again.Should().BeEquivalentTo(new { Created = 0, Updated = 0, Skipped = 2, Failed = 3 });

        // 列的对应关系不对时整体拒绝。
        (await Problem(Import(client, "客户.csv", csv, CrmRecordType.Company, ["industry"]))).Should().Be((400, "validation_failed"));
        (await Problem(Import(client, "客户.csv", csv, CrmRecordType.Company, ["name", "name"]))).Should().Be((400, "validation_failed"));
        (await Problem(Import(client, "客户.csv", csv, CrmRecordType.Company, ["name", "custom.nope"]))).Should().Be((400, "validation_failed"));
        (await Problem(Import(client, "客户.xls", csv, CrmRecordType.Company, ["name"]))).Should().Be((400, "file.unsupported"));
        (await Problem(Import(client, "客户.csv", "名称\n", CrmRecordType.Company, ["name"]))).Should().Be((400, "file.empty"));
        (await Problem(Import(client, "商机.csv", csv, CrmRecordType.Deal, ["name"]))).Should().Be((400, "validation_failed"));
    }

    [Fact]
    public async Task Contact_import_from_excel_matches_companies_and_emails()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        await PostAsync<CrmFieldDto>(client, "/api/crm/fields", Field("wechat", "微信", CrmFieldType.Text, recordType: CrmRecordType.Contact));
        var company = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "星火科技" });
        var other = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "旧东家" });
        var wang = await PostAsync<CrmContactDto>(client, "/api/crm/contacts", new SaveCrmContactRequest { Name = "王经理", Email = "wang@example.com", CompanyId = other.Id });
        var oldDeal = await PostAsync<CrmDealDto>(client, "/api/crm/deals", new SaveCrmDealRequest { Title = "旧东家的项目", ContactId = wang.Id });
        var workbook = Workbook(
            ["姓名", "公司", "邮箱", "职位", "微信"],
            ["王经理", "星火科技", "WANG@example.com", "采购总监", "wx-wang"],
            ["李工", "新客户甲", "li@example.com", "工程师", ""],
            ["赵总", "新客户甲", "zhao@example.com", "", ""],
            ["钱经理", "", "li@example.com", "", ""]);
        string?[] mapping = ["name", "company", "email", "title", "custom.wechat"];

        var preview = await PreviewAsync(client, "联系人.xlsx", workbook, CrmRecordType.Contact);
        preview.Columns.Select(c => c.SuggestedTarget).Should().Equal("name", "company", "email", "title", "custom.wechat");

        // 不自动新建客户时，找不到的客户逐行报错；同一文件里重复的邮箱也报错。
        var strict = await ImportAsync(client, "联系人.xlsx", workbook, CrmRecordType.Contact, mapping, dryRun: true);
        strict.Should().BeEquivalentTo(new { Created = 0, Skipped = 1, Failed = 3, CompaniesCreated = 0 });
        strict.Errors.Select(e => e.Row).Should().Equal(3, 4, 5);
        strict.Errors[0].Message.Should().Contain("找不到客户「新客户甲」");
        strict.Errors[2].Message.Should().Contain("第 3 行");

        var result = await ImportAsync(client, "联系人.xlsx", workbook, CrmRecordType.Contact, mapping, duplicates: CrmImportDuplicateMode.Update, createMissingCompanies: true);
        result.Should().BeEquivalentTo(new { Created = 2, Updated = 1, Failed = 1, CompaniesCreated = 1 });
        var contacts = (await GetAsync<PagedResult<CrmContactDto>>(client, "/api/crm/contacts")).Items.ToDictionary(c => c.Name);
        contacts.Keys.Should().BeEquivalentTo(["王经理", "李工", "赵总"]);
        contacts["王经理"].Id.Should().Be(wang.Id);
        contacts["王经理"].CompanyId.Should().Be(company.Id);
        contacts["王经理"].Title.Should().Be("采购总监");
        contacts["王经理"].CustomFields["wechat"].GetString().Should().Be("wx-wang");
        contacts["李工"].CompanyName.Should().Be("新客户甲");
        contacts["赵总"].CompanyId.Should().Be(contacts["李工"].CompanyId);
        // 换了客户的联系人不再对接原客户的商机。
        (await GetAsync<CrmDealDetailDto>(client, $"/api/crm/deals/{oldDeal.Id}")).Deal.ContactId.Should().BeNull();

        // 客户有必填字段时不能自动新建。
        await PostAsync<CrmFieldDto>(client, "/api/crm/fields", Field("region", "区域", CrmFieldType.Text, required: true));
        var blocked = await ImportAsync(client, "联系人.csv", "姓名,公司\n孙工,新客户乙\n", CrmRecordType.Contact, ["name", "company"], createMissingCompanies: true);
        blocked.Failed.Should().Be(1);
        blocked.Errors[0].Message.Should().Contain("必填字段（区域）");
    }

    private static SaveCrmFieldRequest Field(
        string key,
        string label,
        CrmFieldType type,
        CrmRecordType recordType = CrmRecordType.Company,
        bool required = false,
        string[]? options = null) => new()
    {
        RecordType = recordType,
        Key = key,
        Label = label,
        Type = type,
        Required = required,
        Options = options ?? [],
    };

    private static Dictionary<string, JsonElement> Values(object values)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values, PlatformJson.Options), PlatformJson.Options)!;

    private static Dictionary<string, string> Raw(IReadOnlyDictionary<string, JsonElement> values)
        => values.ToDictionary(v => v.Key, v => JsonSerializer.Serialize(v.Value, PlatformJson.Options));

    private static async Task<(int Status, string? Code)> Problem(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        var body = await response.Content.ReadAsStringAsync(Ct);
        var code = body.Length > 0 && JsonDocument.Parse(body).RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
        return ((int)response.StatusCode, code);
    }

    private static async Task<Dictionary<string, string[]>> ErrorsAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("errors").Deserialize<Dictionary<string, string[]>>()!;
    }

    private static async Task<CrmImportPreviewDto> PreviewAsync(HttpClient client, string fileName, object content, CrmRecordType recordType)
    {
        var form = new MultipartFormDataContent { { FileContent(content), "file", fileName }, { new StringContent(recordType.ToString().ToLowerInvariant()), "recordType" } };
        using var response = await client.PostAsync(new Uri("/api/crm/import/preview", UriKind.Relative), form, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CrmImportPreviewDto>(PlatformJson.Options, Ct))!;
    }

    private static async Task<CrmImportResultDto> ImportAsync(
        HttpClient client,
        string fileName,
        object content,
        CrmRecordType recordType,
        string?[] mapping,
        CrmImportDuplicateMode duplicates = CrmImportDuplicateMode.Skip,
        bool createMissingCompanies = false,
        bool dryRun = false)
    {
        using var response = await Import(client, fileName, content, recordType, mapping, duplicates, createMissingCompanies, dryRun);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CrmImportResultDto>(PlatformJson.Options, Ct))!;
    }

    private static Task<HttpResponseMessage> Import(
        HttpClient client,
        string fileName,
        object content,
        CrmRecordType recordType,
        string?[] mapping,
        CrmImportDuplicateMode duplicates = CrmImportDuplicateMode.Skip,
        bool createMissingCompanies = false,
        bool dryRun = false)
    {
        var form = new MultipartFormDataContent
        {
            { FileContent(content), "file", fileName },
            { new StringContent(recordType.ToString().ToLowerInvariant()), "recordType" },
            { new StringContent(duplicates.ToString().ToLowerInvariant()), "duplicates" },
            { new StringContent(createMissingCompanies ? "true" : "false"), "createMissingCompanies" },
            { new StringContent(dryRun ? "true" : "false"), "dryRun" },
        };
        foreach (var target in mapping)
        {
            form.Add(new StringContent(target ?? string.Empty), "mapping");
        }

        return client.PostAsync(new Uri("/api/crm/import", UriKind.Relative), form, Ct);
    }

    private static ByteArrayContent FileContent(object content)
    {
        var file = new ByteArrayContent(content as byte[] ?? Encoding.UTF8.GetBytes((string)content));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return file;
    }

    private static byte[] Workbook(params string[][] rows)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new S.Workbook();
            var sheet = workbook.AddNewPart<WorksheetPart>();
            sheet.Worksheet = new S.Worksheet(new S.SheetData(rows.Select((cells, r) => new S.Row(cells.Select((text, c) => new S.Cell
            {
                CellReference = $"{(char)('A' + c)}{r + 1}",
                DataType = S.CellValues.InlineString,
                InlineString = new S.InlineString(new S.Text(text)),
            })) { RowIndex = (uint)(r + 1) }).Cast<OpenXmlElement>()));
            workbook.Workbook.AppendChild(new S.Sheets(new S.Sheet { Name = "联系人", SheetId = 1, Id = workbook.GetIdOfPart(sheet) }));
        }

        return stream.ToArray();
    }
}
