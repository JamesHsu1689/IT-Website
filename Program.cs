using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Razor Pages + automatic antiforgery validation on unsafe methods (POST, etc.)
builder.Services.AddRazorPages(options =>
{
    options.Conventions.ConfigureFilter(new AutoValidateAntiforgeryTokenAttribute());
    
});

// Named rate limit policy for Contact
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("contact", context =>
    {
        // Uses the app's view of the client IP.
        // (We’ll also add Host gating so direct Render-origin spam is less likely.)
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Token bucket: allow small bursts but not sustained spam
        return RateLimitPartition.GetTokenBucketLimiter(ip, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 5,                 // burst
            TokensPerPeriod = 2,            // refill rate
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});

var app = builder.Build();

// Important when app is behind a proxy (e.g. Render) to get correct client IP and scheme for rate limiting and Host gating
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto,

    // Only allow forwarded headers from known networks (recommended)
    KnownIPNetworks = { }, // clears default
    KnownProxies = { }   // clears default
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseRateLimiter();        // IMPORTANT: before MapRazorPages

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();