using Azure;
using Azure.Communication.Email;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Org.BouncyCastle.Security;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace ITWebsite.Pages;

[EnableRateLimiting("contact")] // Apply the "contact" rate limit policy to this page
public class ContactModel : PageModel
{
    private readonly IConfiguration _config;
    private readonly IDataProtector _protector;
    private readonly ILogger<ContactModel> _logger;

    // Simple in-memory daily cap (resets if app restarts)
    private static readonly ConcurrentDictionary<string, int> DailyCounts = new();

    public ContactModel(IConfiguration config, IDataProtectionProvider dp, ILogger<ContactModel> logger)
    {
        _config = config;
        _protector = dp.CreateProtector("ContactFormTimeToken.v1");
        _logger = logger;
    }

    // Signed token stored in hidden field
    [BindProperty] public string FormTimeToken { get; set; } = "";

    // Honeypot
    [BindProperty] public string? Website { get; set; }

    [BindProperty, Required, StringLength(80)]
    public string Name { get; set; } = "";

    [BindProperty, StringLength(120)]
    public string? Email { get; set; } = "";

    [BindProperty, StringLength(30)]
    public string? PhoneNumber { get; set; } = "";

    [BindProperty]
    public string? ServiceType { get; set; } = "";

    [BindProperty, Required, StringLength(2000, MinimumLength = 10)]
    public string Message { get; set; } = "";

    [BindProperty]
    public string? DeviceType { get; set; } = "";

    // Property not set; uncomment it in Contact.cshtml if you want to use it
    // [BindProperty]
    // public string? Urgency { get; set; } = "";

    [BindProperty]
    public string? ServiceMode { get; set; } = "";

    [BindProperty, Required, StringLength(20)]
    public string ContactMethod { get; set; } = "";

    [TempData] public bool Sent { get; set; }

    public void OnGet()
    {
        // Create a signed “issued at” timestamp (unix seconds)
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        FormTimeToken = _protector.Protect(now);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // 0) Only accept contact posts for your real domain (blocks direct Render-origin spam)
        if (!IsAllowedHost(Request.Host.Host))
        {
            return NotFound();
        }

        // Test to verify we're correctly seeing client IP in various headers for proper rate limiting and heuristics (can remove in production)
        // var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "null";
        // var cfIp = Request.Headers["CF-Connecting-IP"].ToString();
        // var xff = Request.Headers["X-Forwarded-For"].ToString();

        // _logger.LogInformation("Contact IP Debug: RemoteIp={RemoteIp} CF={CF} XFF={XFF}",
        //     remoteIp, cfIp, xff);

        // 1) Honeypot
        if (!string.IsNullOrWhiteSpace(Website))
        {
            // Pretend success so bots don't learn
            Sent = true;
            return RedirectToPage();
        }

        // 2) Time-to-submit check (reject super-fast bot posts)
        if (!IsHumanTiming(FormTimeToken))
        {
            // Pretend success so bots don't learn
            Sent = true;
            return RedirectToPage();
        }

        // 3) Conditional validation (your existing logic)
        ValidateConditionalRequirements();

        // 4) Extra sanity checks (cheap heuristics)
        ApplyBasicHeuristics();

        if (!ModelState.IsValid) return Page();

        // 5) Cost fuse: global daily cap
        if (!TryConsumeDailyAllowance(maxPerDay: 10))
        {
            // Show a real message to legit users (no email sent)
            ModelState.AddModelError("", "Contact form is temporarily unavailable. Please email us directly.");
            return Page();
        }

        try
        {
            await SendEmailAsync();
            Sent = true;

            // PRG pattern prevents duplicate email on refresh
            return RedirectToPage();
        }
        catch
        {
            ModelState.AddModelError("", "Something went wrong sending your request. Please try again later.");
            return Page();
        }
    }

    private static bool IsAllowedHost(string host)
    {
        host = (host ?? "").Trim().ToLowerInvariant();
        return host == "kairostech.ca" || host == "www.kairostech.ca";
    }

    private bool IsHumanTiming(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        try
        {
            var unprotected = _protector.Unprotect(token);
            if (!long.TryParse(unprotected, out var issuedAtUnix)) return false;

            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var ageSeconds = nowUnix - issuedAtUnix;

            // Too fast? (bots) Too old? (replay)
            if (ageSeconds < 3) return false;
            if (ageSeconds > 60 * 60) return false; // 1 hour
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryConsumeDailyAllowance(int maxPerDay)
    {
        var key = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
        var newValue = DailyCounts.AddOrUpdate(key, 1, (_, current) => current + 1);

        // If exceeded, clamp and fail
        if (newValue > maxPerDay)
        {
            DailyCounts[key] = maxPerDay;
            return false;
        }

        return true;
    }

    private void ApplyBasicHeuristics()
    {
        // Basic link spam heuristic
        var urlCount = Regex.Matches(Message ?? "", @"https?://", RegexOptions.IgnoreCase).Count;
        if (urlCount >= 3)
        {
            ModelState.AddModelError(nameof(Message), "Please remove extra links and try again.");
        }

        // Force trim
        Name = (Name ?? "").Trim();
        Email = (Email ?? "").Trim();
        PhoneNumber = (PhoneNumber ?? "").Trim();
        Message = (Message ?? "").Trim();

        // Basic email format check when present
        if (!string.IsNullOrWhiteSpace(Email) && !new EmailAddressAttribute().IsValid(Email))
        {
            ModelState.AddModelError(nameof(Email), "Please enter a valid email address.");
        }
    }

    private async Task SendEmailAsync()
    {
        var conn = _config["EmailSettings:AcsConnectionString"]
            ?? throw new InvalidOperationException("Missing EmailSettings__AcsConnectionString");

        var from = _config["EmailSettings:FromEmail"]
            ?? throw new InvalidOperationException("Missing EmailSettings__FromEmail");

        var to = _config["EmailSettings:ToEmail"]
            ?? throw new InvalidOperationException("Missing EmailSettings__ToEmail");

        var client = new EmailClient(conn);

        var subject = "New Support Request";

        var body =
$"""
Name: {Name}
Email: {Email}
Phone: {PhoneNumber}
Preferred Contact: {ContactMethod}
Service Type: {ServiceType}
Device Type: {DeviceType}
Service Mode: {ServiceMode}

Message:
{Message}
""";

        var content = new EmailContent(subject)
        {
            PlainText = body
        };

        var message = new EmailMessage(from, to, content);

        var result = await client.SendAsync(WaitUntil.Completed, message);

        if (result.HasValue && result.Value.Status != EmailSendStatus.Succeeded)
        {
            throw new InvalidOperationException($"ACS send failed. Status={result.Value.Status}");
        }
    }

    private void ValidateConditionalRequirements()
    {
        var method = (ContactMethod ?? "").Trim();

        if (string.IsNullOrWhiteSpace(Name))
            ModelState.AddModelError(nameof(Name), "Name is required.");

        if (string.IsNullOrWhiteSpace(Message))
            ModelState.AddModelError(nameof(Message), "Message is required.");

        if (method.Equals("Any", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(Email))
                ModelState.AddModelError(nameof(Email), "Email is required when contact method is Any.");

            if (string.IsNullOrWhiteSpace(PhoneNumber))
                ModelState.AddModelError(nameof(PhoneNumber), "Phone number is required when contact method is Any.");

            return;
        }

        if (method.Equals("Email", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.Remove(nameof(PhoneNumber));

            if (string.IsNullOrWhiteSpace(Email))
                ModelState.AddModelError(nameof(Email), "Email is required when contact method is Email.");

            return;
        }

        if (method.Equals("Phone call", StringComparison.OrdinalIgnoreCase) ||
            method.Equals("Text", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.Remove(nameof(Email));

            if (string.IsNullOrWhiteSpace(PhoneNumber))
                ModelState.AddModelError(nameof(PhoneNumber), "Phone number is required when contact method is Phone or Text.");

            return;
        }

        ModelState.AddModelError(nameof(ContactMethod), "Please choose a valid contact method.");
    }
}