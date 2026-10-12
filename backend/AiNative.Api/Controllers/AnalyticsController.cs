using AiNative.Application.Analytics;
using AiNative.Contracts.Analytics;
using AiNative.Domain.Analytics;
using Microsoft.AspNetCore.Mvc;

namespace AiNative.Api.Controllers;

[Route("api/analytics")]
public sealed class AnalyticsController : ApiControllerBase
{
    /// <summary>
    /// 当前身份可以查看的指标及其可拆分的维度。
    /// </summary>
    [HttpGet("metrics")]
    public Task<IReadOnlyList<MetricDefinitionDto>> Metrics() => SendAsync(new ListMetricsQuery());

    /// <summary>
    /// 查询一个指标：区间合计、环比、按天/周/月的趋势，可按维度拆分。没有权限查看该指标返回 403。
    /// </summary>
    [HttpGet("query")]
    public Task<MetricResultDto> Query([FromQuery] MetricQueryRequest request) => SendAsync(new QueryMetricQuery(request));

    /// <summary>
    /// 经营概览：关键指标与环比，以及销售漏斗、缺货产品、项目健康度、智能体工作量。
    /// </summary>
    [HttpGet("overview")]
    public Task<AnalyticsOverviewDto> Overview(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] AnalyticsRange? range,
        [FromQuery] string? timeZone)
        => SendAsync(new GetAnalyticsOverviewQuery(from, to, range, timeZone));

    [HttpGet("reports")]
    public Task<IReadOnlyList<AnalyticsReportDto>> Reports() => SendAsync(new ListAnalyticsReportsQuery());

    [HttpGet("reports/{id:guid}")]
    public Task<AnalyticsReportDto> GetReport(Guid id) => SendAsync(new GetAnalyticsReportQuery(id));

    [HttpPost("reports")]
    public Task<AnalyticsReportDto> CreateReport(SaveAnalyticsReportRequest request) => SendAsync(new CreateAnalyticsReportCommand(request));

    [HttpPut("reports/{id:guid}")]
    public Task<AnalyticsReportDto> UpdateReport(Guid id, SaveAnalyticsReportRequest request) => SendAsync(new UpdateAnalyticsReportCommand(id, request));

    [HttpDelete("reports/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteReport(Guid id)
    {
        await SendAsync(new DeleteAnalyticsReportCommand(id)).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// 按给定顺序重排报表，没列出的排在后面。
    /// </summary>
    [HttpPut("reports/order")]
    public Task<IReadOnlyList<AnalyticsReportDto>> ReorderReports(ReorderAnalyticsReportsRequest request)
        => SendAsync(new ReorderAnalyticsReportsCommand(request.Ids));
}
