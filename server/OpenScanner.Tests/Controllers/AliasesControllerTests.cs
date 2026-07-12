using Microsoft.AspNetCore.Mvc;
using Moq;
using OpenScanner.Server.Controllers;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using Xunit;

namespace OpenScanner.Tests.Controllers;

public class AliasesControllerTests
{
    private readonly Mock<IDatabase> _dbMock = new();
    private readonly AliasesController _controller;

    public AliasesControllerTests()
    {
        _controller = new AliasesController(_dbMock.Object);
    }

    [Fact]
    public async Task GetAliases_ReturnsAliases()
    {
        _dbMock.Setup(db => db.GetAliasesAsync())
            .ReturnsAsync(new[] { new RadioAlias { Kind = "SRC", Value = 101, Name = "Car 1", AlphaTag = "PD", Frequency = 155.0 } });

        var result = await _controller.GetAliases();

        Assert.Single(result);
    }

    [Fact]
    public async Task GetCandidates_PassesDaysThrough()
    {
        _dbMock.Setup(db => db.GetAliasCandidatesAsync(7)).ReturnsAsync(Array.Empty<AliasCandidate>());

        await _controller.GetCandidates(7);

        _dbMock.Verify(db => db.GetAliasCandidatesAsync(7), Times.Once);
    }

    [Fact]
    public async Task AddAlias_ReturnsCreatedWithId()
    {
        var alias = new RadioAlias { Kind = "TG", Value = 4021, Name = "Dispatch", AlphaTag = "FD", Frequency = 154.4 };
        _dbMock.Setup(db => db.AddAliasAsync(alias)).ReturnsAsync(9);

        var result = await _controller.AddAlias(alias) as CreatedAtActionResult;

        Assert.NotNull(result);
        Assert.Equal(9, ((RadioAlias)result!.Value!).Id);
    }

    [Fact]
    public async Task UpdateAlias_SetsIdAndUpdates()
    {
        var alias = new RadioAlias { Kind = "SRC", Value = 5, Name = "Engine 1", AlphaTag = "FD", Frequency = 154.4 };

        var result = await _controller.UpdateAlias(3, alias) as OkResult;

        Assert.NotNull(result);
        _dbMock.Verify(db => db.UpdateAliasAsync(It.Is<RadioAlias>(a => a.Id == 3 && a.Name == "Engine 1")), Times.Once);
    }

    [Fact]
    public async Task DeleteAlias_Deletes()
    {
        var result = await _controller.DeleteAlias(3) as OkResult;

        Assert.NotNull(result);
        _dbMock.Verify(db => db.DeleteAliasAsync(3), Times.Once);
    }
}
