using Auraly.Platform.Application.Auth.DTOs;
using Auraly.Platform.Application.Auth.Interfaces;
using Auraly.Platform.Application.Common.Exceptions;
using Auraly.Platform.Application.Common.Interfaces;
using Auraly.Platform.Domain.Entities;
using Auraly.Platform.Domain.Repositories;

namespace Auraly.Platform.Application.Auth.Services;

public sealed class AuthService(
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher) : IAuthService
{
    public async Task ChangePasswordAsync(
        Guid userId,
        ChangePasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.CurrentPassword))
            throw new DomainValidationException(
                "currentPassword", "La contraseña actual es obligatoria.");
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 10)
            throw new DomainValidationException(
                "newPassword", "La nueva contraseña debe tener al menos 10 caracteres.");

        var user = await unitOfWork.AppUsers.GetByIdAsync(
            userId, cancellationToken)
            ?? throw new NotFoundException(nameof(AppUser), userId);

        if (string.IsNullOrEmpty(user.PasswordHash))
            throw new InvalidOperationException(
                "La cuenta no tiene contraseña local configurada.");
        if (!passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
            throw new UnauthorizedAccessException(
                "La contraseña actual es incorrecta.");

        user.PasswordHash = passwordHasher.Hash(request.NewPassword);
        user.PosOfflinePasswordSalt = null;
        user.PosOfflinePasswordHash = null;
        user.PosOfflinePasswordIterations = null;
        user.PosOfflinePasswordChangedAt = null;
        user.UpdatedAt = DateTime.UtcNow;
        unitOfWork.AppUsers.Update(user);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
