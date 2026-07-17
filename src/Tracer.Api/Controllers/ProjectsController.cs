using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Contracts;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

[ApiController]
public class ProjectsController(TracerDbContext db) : ControllerBase
{
    [HttpGet("api/teams/{teamId:guid}/projects")]
    public async Task<ActionResult<List<ProjectDto>>> ListForTeam(Guid teamId)
    {
        if (!await db.Teams.AnyAsync(t => t.Id == teamId))
        {
            return NotFound();
        }

        var projects = await db.Projects
            .Where(p => p.TeamId == teamId)
            .OrderBy(p => p.CreatedAt)
            .Select(p => new ProjectDto(p.Id, p.TeamId, p.Name, p.Description, p.CreatedAt))
            .ToListAsync();
        return Ok(projects);
    }

    [HttpPost("api/teams/{teamId:guid}/projects")]
    public async Task<ActionResult<ProjectDto>> Create(Guid teamId, CreateProjectRequest request)
    {
        if (!await db.Teams.AnyAsync(t => t.Id == teamId))
        {
            return NotFound();
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
        if (project is null)
        {
            return NotFound();
        }

        return Ok(new ProjectDto(project.Id, project.TeamId, project.Name, project.Description, project.CreatedAt));
    }

    [HttpPut("api/projects/{id:guid}")]
    public async Task<ActionResult<ProjectDto>> Update(Guid id, UpdateProjectRequest request)
    {
        var project = await db.Projects.FindAsync(id);
        if (project is null)
        {
            return NotFound();
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
        if (project is null)
        {
            return NotFound();
        }

        db.Projects.Remove(project);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
