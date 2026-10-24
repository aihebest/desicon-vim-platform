using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Domain.VendorAccess;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Vim.Infrastructure.Persistence;

public sealed class EfVendorAccessRepository(VimDbContext db) : IVendorAccessRepository
{
    public Task<VendorInvitation?> GetInvitationByTokenHashAsync(string tokenHash, CancellationToken ct) =>
        db.VendorInvitations.SingleOrDefaultAsync(i => i.TokenHash == tokenHash, ct);

    public Task<VendorInvitation?> GetInvitationAsync(Guid id, CancellationToken ct) =>
        db.VendorInvitations.SingleOrDefaultAsync(i => i.Id == id, ct);

    public async Task<IReadOnlyList<VendorInvitation>> ListPendingInvitationsAsync(DateTimeOffset nowUtc, string? vendorNo, CancellationToken ct) =>
        await db.VendorInvitations
            .Where(i => i.AcceptedAtUtc == null && i.RevokedAtUtc == null && i.ExpiresAtUtc > nowUtc
                && (vendorNo == null || i.VendorNo == vendorNo))
            .OrderByDescending(i => i.CreatedAtUtc)
            .Take(500)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public void AddInvitation(VendorInvitation invitation) => db.VendorInvitations.Add(invitation);

    public Task<VendorUser?> GetUserByExternalIdAsync(string externalUserId, CancellationToken ct) =>
        db.VendorUsers.SingleOrDefaultAsync(u => u.ExternalUserId == externalUserId, ct);

    public Task<VendorUser?> GetUserAsync(Guid id, CancellationToken ct) =>
        db.VendorUsers.SingleOrDefaultAsync(u => u.Id == id, ct);

    public async Task<IReadOnlyList<VendorUser>> ListUsersAsync(string? vendorNo, CancellationToken ct) =>
        await db.VendorUsers
            .Where(u => vendorNo == null || u.VendorNo == vendorNo)
            .OrderBy(u => u.VendorNo)
            .ThenBy(u => u.Email)
            .Take(2000)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public void AddUser(VendorUser user) => db.VendorUsers.Add(user);

    public void AddEvent(VendorAccessEvent accessEvent) => db.VendorAccessEvents.Add(accessEvent);

    public async Task<IReadOnlyList<VendorAccessEvent>> ListEventsAsync(string? vendorNo, int take, CancellationToken ct) =>
        await db.VendorAccessEvents
            .AsNoTracking()
            .Where(e => vendorNo == null || e.VendorNo == vendorNo)
            .OrderByDescending(e => e.OccurredAtUtc)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct)
            .ConfigureAwait(false);
}
