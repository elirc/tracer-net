using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Auth;
using Tracer.Api.Contracts;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

[ApiController]
public class ProjectsController(TracerDbContext db, TeamAccess access) : ControllerBase
{
    [HttpGet("api/teams/{teamId:guid}/projects")]
    public async Task<ActionResult<PagedResult<ProjectDto>>> ListForTeam(Guid teamId, [FromQuery] PageQuery paging)
    {
        if (!await access.CanAccessTeamAsync(User, teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        var projects = await db.Projects
            .Where(p => p.TeamId == teamId)
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .Select(p => new ProjectDto(p.Id, p.TeamId, p.Name, p.Description, p.CreatedAt))
            .ToPagedResultAsync(paging);
        return Ok(projects);
    }

    [HttpPost("api/teams/{teamId:guid}/projects")]
    public async Task<ActionResult<ProjectDto>> Create(Guid teamId, CreateProjectRequest request)
    {
        if (!await access.CanAccessTeamAsync(User, teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        var project = new Project { TeamId = teamId, Name = request.Name, Description = request.Description };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var dto = new ProjectDto(project.Id, project.TeamId, project.Name, project.Description, project.CreatedAt);
        return CreatedAtAction(nameof(Get), new { id = project.Id }, dto);
    }

    [HttpGet("api/projects/{id:guid}")]
    public async Task<ActionResult<ProjectDto>> Get(Guid id)
    {
        var project = await db.Projects.FindAsync(id);
        if (project is null || !await access.CanAccessTeamAsync(User, project.TeamId))
        {
            return this.NotFoundProblem("Project", id);
        }

        return Ok(new ProjectDto(project.Id, project.TeamId, project.Name, project.Description, project.CreatedAt));
    }

    [HttpPut("api/projects/{id:guid}")]
    public async Task<ActionResult<ProjectDto>> Update(Guid id, UpdateProjectRequest request)
    {
        var project = await db.Projects.FindAsync(id);
        if (project is null || !await access.CanAccessTeamAsync(User, project.TeamId))
        {
            return this.NotFoundProblem("Project", id);
        }

        project.Name = request.Name;
        project.Description = request.Description;
        await db.SaveChangesAsync();

        return Ok(new ProjectDto(project.Id, project.TeamId, project.Name, project.Description, project.CreatedAt));
    }

    [HttpDelete("api/projects/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var project = await db.Projects.FindAsync(id);
        if (project is null || !await access.CanAccessTeamAsync(User, project.TeamId))
        {
            return this.NotFoundProblem("Project", id);
        }

        db.Projects.Remove(project);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
