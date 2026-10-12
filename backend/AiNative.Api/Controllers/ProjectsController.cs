using AiNative.Application.Projects;
using AiNative.Contracts.Common;
using AiNative.Contracts.Projects;
using AiNative.Domain.Projects;
using Microsoft.AspNetCore.Mvc;

namespace AiNative.Api.Controllers;

[Route("api/projects")]
public sealed class ProjectsController : ApiControllerBase
{
    /// <summary>
    /// 项目列表：进行中的在前，同状态按目标日期排列；search 匹配名称和说明，openOnly 只看未结束的。
    /// </summary>
    [HttpGet]
    public Task<PagedResult<ProjectDto>> List(
        [FromQuery] string? search,
        [FromQuery] ProjectStatus? status,
        [FromQuery] bool openOnly = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
        => SendAsync(new ListProjectsQuery(search, status, openOnly, page, pageSize));

    [HttpGet("{id:guid}")]
    public Task<ProjectDetailDto> Get(Guid id) => SendAsync(new GetProjectQuery(id));

    /// <summary>
    /// 项目排期（甘特图数据）：任务起止日期、项目内的依赖、排期冲突和关键路径。需要同时有项目和任务的查看权限。
    /// </summary>
    [HttpGet("{id:guid}/schedule")]
    public Task<ProjectScheduleDto> Schedule(Guid id) => SendAsync(new GetProjectScheduleQuery(id));

    [HttpPost]
    public Task<ProjectDetailDto> Create(SaveProjectRequest request) => SendAsync(new CreateProjectCommand(request));

    /// <summary>
    /// 修改项目。状态改为 completed 要求项目里的任务都已关闭，否则返回 409 project.open_tasks。
    /// </summary>
    [HttpPut("{id:guid}")]
    public Task<ProjectDto> Update(Guid id, SaveProjectRequest request) => SendAsync(new UpdateProjectCommand(id, request));

    /// <summary>
    /// 删除项目。里程碑和成员一并删除，项目里的任务保留，只是不再属于任何项目。
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await SendAsync(new DeleteProjectCommand(id)).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("{id:guid}/milestones")]
    public Task<ProjectMilestoneDto> AddMilestone(Guid id, SaveProjectMilestoneRequest request) => SendAsync(new AddProjectMilestoneCommand(id, request));

    [HttpPut("{id:guid}/milestones/{milestoneId:guid}")]
    public Task<ProjectMilestoneDto> UpdateMilestone(Guid id, Guid milestoneId, SaveProjectMilestoneRequest request)
        => SendAsync(new UpdateProjectMilestoneCommand(id, milestoneId, request));

    /// <summary>
    /// 删除里程碑。里面的任务留在项目里，只是不再属于这个里程碑。
    /// </summary>
    [HttpDelete("{id:guid}/milestones/{milestoneId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteMilestone(Guid id, Guid milestoneId)
    {
        await SendAsync(new DeleteProjectMilestoneCommand(id, milestoneId)).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// 标记里程碑完成。要求里程碑下的任务都已关闭，否则返回 409 project_milestone.open_tasks。
    /// </summary>
    [HttpPost("{id:guid}/milestones/{milestoneId:guid}/complete")]
    public Task<ProjectMilestoneDto> CompleteMilestone(Guid id, Guid milestoneId) => SendAsync(new SetProjectMilestoneCompletedCommand(id, milestoneId, true));

    [HttpPost("{id:guid}/milestones/{milestoneId:guid}/reopen")]
    public Task<ProjectMilestoneDto> ReopenMilestone(Guid id, Guid milestoneId) => SendAsync(new SetProjectMilestoneCompletedCommand(id, milestoneId, false));

    [HttpPost("{id:guid}/members")]
    public Task<ProjectMemberDto> AddMember(Guid id, AddProjectMemberRequest request) => SendAsync(new AddProjectMemberCommand(id, request));

    [HttpPut("{id:guid}/members/{memberId:guid}")]
    public Task<ProjectMemberDto> UpdateMember(Guid id, Guid memberId, UpdateProjectMemberRequest request) => SendAsync(new UpdateProjectMemberCommand(id, memberId, request));

    /// <summary>
    /// 移出项目成员。项目负责人不能直接移出，需要先更换负责人。
    /// </summary>
    [HttpDelete("{id:guid}/members/{memberId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveMember(Guid id, Guid memberId)
    {
        await SendAsync(new RemoveProjectMemberCommand(id, memberId)).ConfigureAwait(false);
        return NoContent();
    }
}
