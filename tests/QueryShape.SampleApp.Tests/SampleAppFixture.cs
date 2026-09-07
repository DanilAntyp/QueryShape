using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace QueryShape.SampleApp.Tests;

/// <summary>Boots the sample app in-process and records every request scope's diagnoses through a listener.</summary>
public sealed class SampleAppFixture : IDisposable
{
    public SampleAppFixture()
    {
        Listener = new ScopeListener();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureServices(services =>
            {
                services.AddQueryShape(o =>
                {
                    o.CaptureCallSites = true;
                    o.Listeners.Add(Listener);
                });
            });
        });
        Client = Factory.CreateClient();
    }

    public WebApplicationFactory<Program> Factory { get; }

    public HttpClient Client { get; }

    public ScopeListener Listener { get; }

    /// <summary>GETs a path and returns the scope the middleware opened for it, with its diagnoses.</summary>
    public async Task<(HttpResponseMessage Response, QueryShapeScope Scope, IReadOnlyList<Diagnosis> Diagnoses)> GetAsync(string path)
    {
        var response = await Client.GetAsync(new Uri(path, UriKind.Relative));
        var scopeName = "GET " + path;
        var entry = Listener.Completed.Reverse().First(e => e.Scope.Name == scopeName);
        return (response, entry.Scope, entry.Diagnoses);
    }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
    }

    public sealed class ScopeListener : IQueryShapeListener
    {
        public ConcurrentQueue<(QueryShapeScope Scope, IReadOnlyList<Diagnosis> Diagnoses)> Completed { get; } = new();

        public void OnCommandCaptured(CapturedCommand command, QueryShapeScope? scope)
        {
        }

        public void OnScopeCompleted(QueryShapeScope scope, IReadOnlyList<Diagnosis> diagnoses) => Completed.Enqueue((scope, diagnoses));
    }
}
