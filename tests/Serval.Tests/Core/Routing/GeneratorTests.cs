#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Serval.Authentication.Authorization;
using Serval.Generators;

namespace Serval.Tests.Core.Routing;

public sealed class GeneratorTests
{
    [Fact]
    public void AssembliesWithoutControllersDoNotDeclareRegistrationExtensions()
    {
        var (compilation, diagnostics) = Generate("public class Empty {}");
        Assert.Empty(diagnostics);
        Assert.Single(compilation.SyntaxTrees);
    }

    [Theory]
    [InlineData("internal class Demo", "public ValueTask<IActionResult> GetAsync() => default;", "SERVAL001")]
    [InlineData("public abstract class Demo", "public ValueTask<IActionResult> GetAsync() => default;", "SERVAL001")]
    [InlineData("public class Demo", "public void GetAsync() {}", "SERVAL002")]
    [InlineData("public class Demo", "public IActionResult Get() => null!;", "SERVAL002")]
    [InlineData("public class Demo", "public Serval.Http.Results.TextResult Get() => new(\"hello\");", "SERVAL002")]
    [InlineData("public class Demo", "public async void GetAsync() { await Task.Yield(); }", "SERVAL002")]
    [InlineData("public class Demo", "public static ValueTask GetAsync() => ValueTask.CompletedTask;", "SERVAL002")]
    [InlineData("public class Demo", "public ValueTask GetAsync(int id) => ValueTask.CompletedTask;", "SERVAL002")]
    [InlineData("public class Demo", "public ValueTask GetAsync() => ValueTask.CompletedTask;", "SERVAL002")]
    [InlineData("public class Demo", "public Task<string> GetAsync() => Task.FromResult(\"value\");", "SERVAL002")]
    [InlineData("public class Demo", "public ValueTask<int> GetAsync() => ValueTask.FromResult(1);", "SERVAL002")]
    [InlineData("public class Demo", "public Task<IActionResult?> GetAsync() => Task.FromResult<IActionResult?>(null);", "SERVAL002")]
    [InlineData("public class Demo", "public ValueTask<IActionResult?> GetAsync() => ValueTask.FromResult<IActionResult?>(null);", "SERVAL002")]
    [InlineData("public class Demo", "public ValueTask<IActionResult> GetAsync(int value) => default;", "SERVAL002")]
    public void InvalidControllersAndSynchronousActionsProduceDiagnostics(string declaration, string method, string diagnostic)
    {
        var (_, diagnostics) = Generate($$"""
                                          using System.Threading.Tasks;
                                          using Serval.Http;
                                          using Serval.Routing;
                                          [Controller]
                                          {{declaration}}
                                          {
                                              [Get("test")]
                                              {{method}}
                                          }
                                          """);
        Assert.Contains(diagnostics, entry => entry.Id == diagnostic);
    }

    [Fact]
    public void RegistrationPreservesClassSlashesAndUsesModernExtensionSyntax()
    {
        var (compilation, diagnostics) = Generate("""
                                                  using System.Threading.Tasks;
                                                  using Serval;
                                                  using Serval.Http;
                                                  using Serval.Routing;
                                                  [Controller, Route("/api/")]
                                                  public class Demo
                                                  {
                                                      [Get]
                                                      public ValueTask<IActionResult> GetAsync(RequestContext context) => ValueTask.FromResult<IActionResult>(new Serval.Http.Results.TextResult("hello"));
                                                  }
                                                  """);
        Assert.DoesNotContain(diagnostics, entry => entry.Severity                  == DiagnosticSeverity.Error);
        Assert.DoesNotContain(compilation.GetDiagnostics(), entry => entry.Severity == DiagnosticSeverity.Error);
        var source = compilation.SyntaxTrees.Last().ToString();
        Assert.Contains("/api/", source);
        Assert.Contains("public static class @GeneratorTestControllers", source);
        Assert.Contains("public static global::Serval.Hosting.ServalBuilder Register(", source);
        Assert.DoesNotContain("IServalModule", source);
        Assert.DoesNotContain("AddModule", source);
        Assert.Contains("extension(global::Serval.Hosting.ServalBuilder builder)", source);
        Assert.Contains("controller.@GetAsync(context)", source);
        Assert.DoesNotContain("WriteJsonAsync", source);
    }

    [Theory]
    [InlineData("Task<IActionResult>", "Task.FromResult<IActionResult>(new TextResult(\"hello\"))")]
    [InlineData("ValueTask<IActionResult>", "ValueTask.FromResult<IActionResult>(new TextResult(\"hello\"))")]
    [InlineData("Task<TextResult>", "Task.FromResult(new TextResult(\"hello\"))")]
    [InlineData("ValueTask<TextResult>", "ValueTask.FromResult(new TextResult(\"hello\"))")]
    public void AsyncResultContractsCompileWithoutReflectionDispatch(string returnType, string expression)
    {
        var (compilation, diagnostics) = Generate($$"""
                                                    using System.Threading.Tasks;
                                                    using Serval.Http;
                                                    using Serval.Http.Results;
                                                    using Serval.Routing;
                                                    [Controller]
                                                    public class Demo
                                                    {
                                                        [Get("hello")]
                                                        public {{returnType}} GetAsync() => {{expression}};
                                                    }
                                                    """);
        Assert.DoesNotContain(diagnostics, entry => entry.Severity                  == DiagnosticSeverity.Error);
        Assert.DoesNotContain(compilation.GetDiagnostics(), entry => entry.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void AuthenticationAttributesArePreservedOnGeneratedRoutes()
    {
        var (compilation, diagnostics) = Generate("""
                                                  using System.Threading.Tasks;
                                                  using Serval;
                                                  using Serval.Authentication.Authorization;
                                                  using Serval.Authentication.Cookies;
                                                  using Serval.Http;
                                                  using Serval.Routing;
                                                  [Controller]
                                                  [Authorize(Scheme = "Cookies", Policy = "member", Priority = -10)]
                                                  public class SecureController
                                                  {
                                                      [Post("private")]
                                                      [RequireCsrf(HeaderName = "X-Custom-Csrf")]
                                                      public ValueTask<IActionResult> Action(RequestContext context) => ValueTask.FromResult<IActionResult>(new Serval.Http.Results.TextResult("ok"));
                                                  }
                                                  """);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity                  == DiagnosticSeverity.Error);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var generated = compilation.SyntaxTrees.Last().ToString();
        Assert.Contains("new global::Serval.Authentication.Authorization.AuthorizeAttribute()", generated);
        Assert.Contains("@Scheme = \"Cookies\"", generated);
        Assert.Contains("@Priority = -10", generated);
        Assert.Contains("new global::Serval.Authentication.Cookies.RequireCsrfAttribute()", generated);
    }

    [Fact]
    public void MiddlewarePriorityConstructorsAndPropertyOverridesArePreserved()
    {
        var (compilation, diagnostics) = Generate("""
                                                  using System.Threading.Tasks;
                                                  using Serval;
                                                  using Serval.Http;
                                                  using Serval.Middleware;
                                                  using Serval.Routing;

                                                  public sealed class TraceAttribute(string name, int priority = 0) : MiddlewareAttribute(priority)
                                                  {
                                                      public override ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
                                                      {
                                                          context.Items[name] = Priority;
                                                          return next(context);
                                                      }
                                                  }

                                                  [Controller, Trace("controller", priority: -100)]
                                                  public class Demo
                                                  {
                                                      [Get("test")]
                                                      [Trace("default")]
                                                      [Trace("override", 100, Priority = -200)]
                                                      public ValueTask<IActionResult> GetAsync(RequestContext context) => ValueTask.FromResult<IActionResult>(new Serval.Http.Results.TextResult("ok"));
                                                  }
                                                  """);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity                  == DiagnosticSeverity.Error);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var generated = compilation.SyntaxTrees.Last().ToString();
        Assert.Contains("new global::TraceAttribute(\"controller\", -100)", generated);
        Assert.Contains("new global::TraceAttribute(\"default\", 0)", generated);
        Assert.Contains("new global::TraceAttribute(\"override\", 100) { @Priority = -200 }", generated);
    }

    internal static (Compilation Compilation, IEnumerable<Diagnostic> Diagnostics) Generate(string source)
    {
        var             trusted     = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var             references  = trusted.Append(typeof(RequestContext).Assembly.Location).Append(typeof(AuthorizeAttribute).Assembly.Location).Append(typeof(IServiceCollection).Assembly.Location).Distinct().Select(path => MetadataReference.CreateFromFile(path));
        var             options     = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp15);
        var             compilation = CSharpCompilation.Create("GeneratorTest", [CSharpSyntaxTree.ParseText(source, options)], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver      = CSharpGeneratorDriver.Create([new ControllerGenerator().AsSourceGenerator()], parseOptions: options);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var diagnostics);
        return (generated, diagnostics);
    }
}
