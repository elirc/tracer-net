using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Tracer.Infrastructure;

namespace Tracer.Api.Auth;

/// <summary>
/// Answers "may this caller touch this team?" — the single question every
/// team-scoped endpoint asks before it does anything else.
///
/// <para>
/// <b>Existence and permission give the same answer on purpose.</b>
/// <see cref="CanAccessTeamAsync"/> returns false both for a team that does not
/// exist and for one the caller is not on, and callers turn that into a 404 in
/// both cases. A 403 on someone else's team would confirm the team is real, and
/// an id oracle is still a leak: an attacker who can tell "not yours" from "not
/// there" can enumerate what a workspace contains without ever reading a row.
/// The cost is that a member who genuinely mistypes their own team id is told
/// "not found" rather than "not allowed"; that is the right trade, and it is why
/// this is stated once here rather than re-decided in each controller.
/// </para>
/// <para>
/// Registered scoped, so the membership lookup happens once per request no
/// matter how many resources a handler resolves.
/// </para>
/// </summary>
public sealed class TeamAccess(TracerDbContext db)
{
    private HashSet<Guid>? _memberTeamIds;

    /// <summary>
    /// True when the team exists and the caller may see it. An admin sees every
    /// team in the workspace; a member sees the teams they belong to.
    /// </summary>
    public async Task<bool> CanAccessTeamAsync(ClaimsPrincipal user, Guid teamId)
    {
        if (!await db.Teams.AnyAsync(t => t.Id == teamId))
        {
            return false;
        }

        return user.IsAdmin() || (await MemberTeamIdsAsync(user)).Contains(teamId);
    }

    /// <summary>
    /// The teams a member belongs to. Meaningless for an admin — check
    /// <see cref="ClaimsPrincipalExtensions.IsAdmin"/> first — because an admin's
    /// reach is not expressed as membership rows.
    /// </summary>
    public async Task<HashSet<Guid>> MemberTeamIdsAsync(ClaimsPrincipal user) =>
        _memberTeamIds ??= [.. await db.TeamMemberships
            .Where(m => m.UserId == user.UserId())
            .Select(m => m.TeamId)
            .ToListAsync()];
}
