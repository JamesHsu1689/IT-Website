using Azure;
using Azure.Communication.Email;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace ITWebsite.Pages;

[EnableRateLimiting("contact")]
public class ContactModel : PageModel
{
    private readonly IConfiguration _config;
    private readonly IDataProtector _protector;
    private readonly ILogger<ContactModel> _logger;

    private static readonly ConcurrentDictionary<string, int> DailyCounts = new();

    public ContactModel(IConfiguration config, IDataProtectionProvider dp, ILogger<ContactModel> logger)
    {
        _config = config;
        _protector = dp.CreateProtector("ContactFormTimeToken.v1");
        _logger = logger;
    }

    [BindProperty] public string FormTimeToken { get; set; } = "";

    // Honeypot
    [BindProperty] public string? Website { get; set; }

    [BindProperty, Required, StringLength(80)]
    public string Name { get; set; } = "";

    [BindProperty, StringLength(120)]
    public string? Email { get; set; } = "";

    [BindProperty, StringLength(30)]
    public string? PhoneNumber { get; set; } = "";

    [BindProperty, Required, StringLength(50)]
    public string? ServiceType { get; set; } = "";

    [BindProperty, Required, StringLength(2000, MinimumLength = 10)]
    public string Message { get; set; } = "";

    [BindProperty]
    public string? DeviceType { get; set; } = "";

    [BindProperty]
    public string? ServiceMode { get; set; } = "Not sure";

    [BindProperty, Required, StringLength(20)]
    public string ContactMethod { get; set; } = "Email";

    [BindProperty]
    [Range(typeof(bool), "true", "true")]
    public bool PrivacyConsent { get; set; }

    [TempData] public bool Sent { get; set; }

    // PRG-friendly error message
    [TempData] public string? ErrorMessage { get; set; }

    // Draft fields for PRG on failure (so values persist on redirect)
    [TempData] public string? Draft_Name { get; set; }
    [TempData] public string? Draft_Email { get; set; }
    [TempData] public string? Draft_Phone { get; set; }
    [TempData] public string? Draft_ServiceType { get; set; }
    [TempData] public string? Draft_Message { get; set; }
    [TempData] public string? Draft_DeviceType { get; set; }
    [TempData] public string? Draft_ServiceMode { get; set; }
    [TempData] public string? Draft_ContactMethod { get; set; }
    [TempData] public bool Draft_PrivacyConsent { get; set; }

    public void OnGet()
    {
        // Restore draft values from last failed send (if any)
        if (!string.IsNullOrWhiteSpace(Draft_Name)) Name = Draft_Name!;
        if (!string.IsNullOrWhiteSpace(Draft_Email)) Email = Draft_Email;
        if (!string.IsNullOrWhiteSpace(Draft_Phone)) PhoneNumber = Draft_Phone;
        if (!string.IsNullOrWhiteSpace(Draft_ServiceType)) ServiceType = Draft_ServiceType;
        if (!string.IsNullOrWhiteSpace(Draft_Message)) Message = Draft_Message!;
        if (!string.IsNullOrWhiteSpace(Draft_DeviceType)) DeviceType = Draft_DeviceType;
        if (!string.IsNullOrWhiteSpace(Draft_ServiceMode)) ServiceMode = Draft_ServiceMode;
        if (!string.IsNullOrWhiteSpace(Draft_ContactMethod)) ContactMethod = Draft_ContactMethod!;
        PrivacyConsent = Draft_PrivacyConsent;

        // New signed token for timing check
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        FormTimeToken = _protector.Protect(now);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // 0) Only accept contact posts for your real domain (blocks direct-origin spam)
        if (!IsAllowedHost(Request.Host.Host))
            return NotFound();

        // 1) Honeypot
        if (!string.IsNullOrWhiteSpace(Website))
        {
            Sent = true;
            return RedirectToPage();
        }

        // 2) Time-to-submit check
        if (!IsHumanTiming(FormTimeToken))
        {
            Sent = true;
            return RedirectToPage();
        }

        // 3) Conditional validation
        ValidateConditionalRequirements();

        // 4) Cheap heuristics
        ApplyBasicHeuristics();

        if (!ModelState.IsValid)
        {
            // Validation errors: stay on page so user sees inline errors, and inputs remain
            return Page();
        }

        // 5) Cost fuse: global daily cap
        if (!TryConsumeDailyAllowance(maxPerDay: 20))
        {
            ErrorMessage = "Contact form is temporarily unavailable. Please try again later.";
            SaveDraft();
            return RedirectToPage();
        }

        try
        {
            await SendEmailAsync();
            Sent = true;

            ClearDraft();
            return RedirectToPage(); // PRG prevents resend on refresh
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Contact form send failed. Host={Host} Name={Name} Email={Email}",
                Request.Host.Host, Name, Email);

            ErrorMessage = "Something went wrong sending your request. Please try again later.";

            SaveDraft();
            return RedirectToPage(); // PRG on failure too (refresh won't retry POST)
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

            if (ageSeconds < 3) return false;
            if (ageSeconds > 60 * 60) return false;

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

        if (newValue > maxPerDay)
        {
            DailyCounts[key] = maxPerDay;
            return false;
        }

        return true;
    }

    private void ApplyBasicHeuristics()
    {
        var urlCount = Regex.Matches(Message ?? "", @"https?://", RegexOptions.IgnoreCase).Count;
        if (urlCount >= 3)
            ModelState.AddModelError(nameof(Message), "Please remove extra links and try again.");

        Name = (Name ?? "").Trim();
        Email = (Email ?? "").Trim();
        PhoneNumber = (PhoneNumber ?? "").Trim();
        Message = (Message ?? "").Trim();

        if (!string.IsNullOrWhiteSpace(Email) && !new EmailAddressAttribute().IsValid(Email))
            ModelState.AddModelError(nameof(Email), "Please enter a valid email address.");
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
            throw new InvalidOperationException($"ACS send failed. Status={result.Value.Status}");
    }

    private void ValidateConditionalRequirements()
    {
        var method = (ContactMethod ?? "").Trim();

        if (string.IsNullOrWhiteSpace(Name))
            ModelState.AddModelError(nameof(Name), "Name is required.");

        if (string.IsNullOrWhiteSpace(Message))
            ModelState.AddModelError(nameof(Message), "Message is required.");

        if (string.IsNullOrWhiteSpace(ServiceType))
            ModelState.AddModelError(nameof(ServiceType), "Please choose a service.");

        if (!PrivacyConsent)
            ModelState.AddModelError(nameof(PrivacyConsent), "You must agree to the Privacy Policy.");

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

    private void SaveDraft()
    {
        Draft_Name = Name;
        Draft_Email = Email;
        Draft_Phone = PhoneNumber;
        Draft_ServiceType = ServiceType;
        Draft_Message = Message;
        Draft_DeviceType = DeviceType;
        Draft_ServiceMode = ServiceMode;
        Draft_ContactMethod = ContactMethod;
        Draft_PrivacyConsent = PrivacyConsent;
    }

    private void ClearDraft()
    {
        Draft_Name = null;
        Draft_Email = null;
        Draft_Phone = null;
        Draft_ServiceType = null;
        Draft_Message = null;
        Draft_DeviceType = null;
        Draft_ServiceMode = null;
        Draft_ContactMethod = null;
        Draft_PrivacyConsent = false;
        ErrorMessage = null;
    }
}