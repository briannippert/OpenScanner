using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using Xunit;

namespace OpenScanner.Tests;

public class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/api/channels")]
    [InlineData("/api/history")]
    [InlineData("/swagger")]
    public async Task Get_EndpointsReturnSuccess(string url)
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync(url);
        
        // Swagger might redirect, so we check for OK or Redirect
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Redirect || response.StatusCode == HttpStatusCode.MovedPermanently);
    }
}
