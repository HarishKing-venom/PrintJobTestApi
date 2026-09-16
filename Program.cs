using System.Collections.Concurrent;
using System.Text.Json;

//Test commit

// Test stub for the label printing API: takes incoming data and hands back job ids.
// No BarTender, no database, no auth. Everything is kept in memory and lost on restart.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var jobs = new ConcurrentDictionary<Guid, Job>();

// Accepts either a single object {...}, an array [{...},{...}], or {"items":[...]} / {"data":[...]}.
// Returns one job id per item, in the same order.
app.MapPost("/api/v1/print-jobs", (JsonElement body, HttpContext context) =>
{
    var items = Flatten(body).ToList();
    if (items.Count == 0)
    {
        return Results.BadRequest(new { error = "Send an object, an array of objects, or { \"items\": [ ... ] }." });
    }

    var appId = context.Request.Headers["X-App-Id"].ToString();
    var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();

    var accepted = items.Select(item =>
    {
        var job = new Job(Guid.NewGuid(), string.IsNullOrEmpty(appId) ? null : appId,
            string.IsNullOrEmpty(idempotencyKey) ? null : idempotencyKey, item, DateTime.UtcNow);
        jobs[job.JobId] = job;
        return new { jobId = job.JobId, status = "Queued", statusUrl = $"/api/v1/print-jobs/{job.JobId}" };
    }).ToList();

    return Results.Accepted($"/api/v1/print-jobs/{accepted[0].jobId}", new { count = accepted.Count, jobs = accepted });
});

// Jobs report Completed on the first read, so status polling can be exercised end to end.
app.MapGet("/api/v1/print-jobs/{jobId:guid}", (Guid jobId) =>
    jobs.TryGetValue(jobId, out var job)
        ? Results.Ok(new { jobId, status = "Completed", job.AppId, job.IdempotencyKey, job.CreatedUtc, data = job.Data })
        : Results.NotFound(new { error = $"No job '{jobId}'." }));

app.MapGet("/api/v1/print-jobs", () => Results.Ok(jobs.Values
    .OrderByDescending(j => j.CreatedUtc)
    .Select(j => new { j.JobId, status = "Completed", j.AppId, j.IdempotencyKey, j.CreatedUtc, data = j.Data })));

app.MapDelete("/api/v1/print-jobs", () =>
{
    jobs.Clear();
    return Results.NoContent();
});

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", jobs = jobs.Count }));

app.Run();

static IEnumerable<JsonElement> Flatten(JsonElement body)
{
    if (body.ValueKind == JsonValueKind.Object)
    {
        foreach (var name in new[] { "items", "data", "jobs", "labels", "requests" })
        {
            if (body.TryGetProperty(name, out var nested) && nested.ValueKind == JsonValueKind.Array)
            {
                return nested.EnumerateArray().ToList();
            }
        }

        return [body];
    }

    return body.ValueKind == JsonValueKind.Array ? body.EnumerateArray().ToList() : [];
}

internal sealed record Job(Guid JobId, string? AppId, string? IdempotencyKey, JsonElement Data, DateTime CreatedUtc);
