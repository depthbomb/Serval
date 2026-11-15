using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Serval.Binding;
using Serval.Http;
using Serval.Routing;
using Serval.Tests.Support;
using Serval.Validation;

namespace Serval.Tests.Core.Binding;

public sealed class InputValidationTests
{
    [Fact]
    public async Task GeneratedBindingCollectsFieldErrorsAndDoesNotInvokeAction()
    {
        await using var app = new TestApplication(configureBuilder: builder => builder
                                                                               .AddInputValidator<ValidationTestInput, NameValidator>()
                                                                               .AddInputValidator<ValidationTestInput, NameValidator>()
                                                                               .AddInputValidator<ValidationTestInput, OtherValidator>());
        var response = await app.SendAsync("/validation/body", "POST", "{\"name\":null}", "application/json");
        Assert.Equal(422, response.StatusCode);
        Assert.Null(response.Headers["X-Action"]);
        using var json = JsonDocument.Parse(response.Text);
        Assert.Equal("validation_failed", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("errors").GetProperty("name").GetArrayLength());
    }

    [Fact]
    public async Task ValidInputAndUnregisteredTypesContinueNormally()
    {
        await using var app = new TestApplication(configureBuilder: builder => builder.AddInputValidator<ValidationTestInput, NameValidator>());
        Assert.Equal("ok", (await app.SendAsync("/validation/body", "POST", "{\"name\":\"Reese\"}", "application/json")).Text);
        await using var unregistered = new TestApplication();
        Assert.Equal("ok", (await unregistered.SendAsync("/validation/body", "POST", "{}", "application/json")).Text);
    }

    [Theory]
    [InlineData("application/json", "{\"name\":\"\"}")]
    [InlineData("application/x-www-form-urlencoded", "name=")]
    public async Task ManualValidationUsesTheSameErrorsForFormAndJson(string contentType, string body)
    {
        await using var app      = new TestApplication(configureBuilder: builder => builder.AddInputValidator<ValidationTestInput, NameValidator>());
        var             response = await app.SendAsync("/validation/post", "POST", body, contentType);
        Assert.Equal(422, response.StatusCode);
        Assert.Contains("Name is required.", response.Text);
    }

    [Fact]
    public async Task InvalidJsonStillUsesParsingErrorAndHidesValidatorFailures()
    {
        await using var app = new TestApplication(configureBuilder: builder => builder.AddInputValidator<ValidationTestInput, BrokenValidator>());
        Assert.Equal(400, (await app.SendAsync("/validation/body", "POST", "{", "application/json")).StatusCode);
        var response = await app.SendAsync("/validation/body", "POST", "{}", "application/json");
        Assert.Equal(500, response.StatusCode);
        Assert.DoesNotContain("secret", response.Text);
    }

    private sealed class NameValidator : IInputValidator<ValidationTestInput>
    {
        private readonly ScopedProbe _probe;

        public NameValidator(ScopedProbe probe)
        {
            _probe = probe;
        }

        public async ValueTask ValidateAsync(ValidationTestInput input, ValidationErrors errors, RequestContext context, CancellationToken ct)
        {
            await Task.Yield();
            Assert.Same(_probe, context.Services.GetRequiredService<ScopedProbe>());
            Assert.False(_probe.Disposed);
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(input.Name))
            {
                errors.Add("name", "Name is required.");
            }
        }
    }

    private sealed class OtherValidator : IInputValidator<ValidationTestInput>
    {
        public ValueTask ValidateAsync(ValidationTestInput input, ValidationErrors errors, RequestContext context, CancellationToken ct)
        {
            errors.Add("name", "Another rule failed.");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BrokenValidator : IInputValidator<ValidationTestInput>
    {
        public ValueTask ValidateAsync(ValidationTestInput input, ValidationErrors errors, RequestContext context, CancellationToken ct)
            => throw new InvalidOperationException("secret validator failure");
    }
}

public sealed record ValidationTestInput(string? Name);

[Controller]
[Route("/validation")]
public sealed class ValidationTestController : ControllerBase
{
    [Post("/body")]
    public ValueTask<IActionResult> Body([FromBody] ValidationTestInput input)
    {
        Response.Headers["X-Action"] = "called";
        return TextAsync("ok");
    }

    [Post("/post")]
    public async ValueTask<IActionResult> PostValues()
    {
        var data = await Request.ReadPostAsync();
        await ValidateAsync(new ValidationTestInput(data.Get<string>("name")));
        return Text("ok");
    }
}
