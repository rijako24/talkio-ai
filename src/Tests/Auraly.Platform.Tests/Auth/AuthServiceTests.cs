using Auraly.Platform.Application.Auth.DTOs;
using Auraly.Platform.Application.Auth.Interfaces;
using Auraly.Platform.Application.Auth.Services;
using Auraly.Platform.Application.Common.Exceptions;
using Auraly.Platform.Domain.Entities;
using Auraly.Platform.Domain.Repositories;
using Moq;
using Xunit;

namespace Auraly.Platform.Tests.Auth;

public sealed class AuthServiceTests
{
    [Fact]
    public async Task ChangePasswordAsync_RejectsWeakPasswordBeforeLoadingUser()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var service = new AuthService(unitOfWork.Object, Mock.Of<IPasswordHasher>());

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.ChangePasswordAsync(
                Guid.NewGuid(),
                new ChangePasswordRequest("contraseña-actual", "corta")));

        Assert.Contains("newPassword", exception.Errors.Keys);
        unitOfWork.VerifyGet(item => item.AppUsers, Times.Never);
    }

    [Fact]
    public async Task ChangePasswordAsync_RequiresCurrentPasswordAndChangesOnlyAuthenticatedUser()
    {
        var userId = Guid.NewGuid();
        var user = new AppUser { UserId = userId, PasswordHash = "hash-anterior" };
        var users = new Mock<IAppUserRepository>();
        users.Setup(item => item.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(item => item.AppUsers).Returns(users.Object);
        var passwordHasher = new Mock<IPasswordHasher>();
        passwordHasher.Setup(item => item.Verify("contraseña-actual", "hash-anterior"))
            .Returns(true);
        passwordHasher.Setup(item => item.Hash("contraseña-nueva"))
            .Returns("hash-nuevo");
        var service = new AuthService(unitOfWork.Object, passwordHasher.Object);

        await service.ChangePasswordAsync(
            userId,
            new ChangePasswordRequest("contraseña-actual", "contraseña-nueva"));

        Assert.Equal("hash-nuevo", user.PasswordHash);
        users.Verify(item => item.Update(user), Times.Once);
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
