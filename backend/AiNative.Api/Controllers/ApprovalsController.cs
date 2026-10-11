using AiNative.Application.Governance;
using AiNative.Contracts.Governance;
using AiNative.Domain.Governance;
using Microsoft.AspNetCore.Mvc;

namespace AiNative.Api.Controllers;

[Route("api/approvals")]
public sealed class ApprovalsController : ApiControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<ApprovalDto>> List([FromQuery] ApprovalStatus? status) => SendAsync(new ListApprovalsQuery(status));

    /// <summary>
    /// 批准或驳回。批准后立即以申请者身份执行该动作，并唤醒申请的智能体继续工作。
    /// </summary>
    [HttpPost("{id:guid}/decision")]
    public Task<ApprovalDto> Decide(Guid id, DecideApprovalRequest request) => SendAsync(new DecideApprovalCommand(id, request));
}
