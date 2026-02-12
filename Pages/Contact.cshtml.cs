using Azure;
using Azure.Communication.Email;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ITWebsite.Pages;

public class ContactModel : PageModel
{
    private readonly IConfiguration _config;

    public ContactModel(IConfiguration config) => _config = config;

    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string? Email { get; set; } = "";
    [BindProperty] public string? PhoneNumber { get; set; } = "";
    [BindProperty] public string Message { get; set; } = "";
    [BindProperty] public string ContactMethod { get; set; } = "";

    [TempData]
    public bool Sent { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        ValidateConditionalRequirements();

        if (!ModelState.IsValid) return Page();

        try
        {
            await SendEmailAsync();

            Sent = true;

            // PRG pattern: prevents duplicate email on refresh
            return RedirectToPage();
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", $"Email failed: {ex.Message}");
            return Page();
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

    Message:
    {Message}
    """;

        var content = new EmailContent(subject)
        {
            PlainText = body
        };

        var message = new EmailMessage(
            senderAddress: from,
            recipientAddress: to,
            content: content
        );

        var result = await client.SendAsync(WaitUntil.Completed, message);

        if (result.HasValue && result.Value.Status != EmailSendStatus.Succeeded)
        {
            throw new InvalidOperationException($"ACS send failed. Status={result.Value.Status}");
        }
    }


    // This method performs conditional validation based on the selected contact method.
    private void ValidateConditionalRequirements()
    {
        var method = (ContactMethod ?? "").Trim();

        if (string.IsNullOrWhiteSpace(Name))
            ModelState.AddModelError(nameof(Name), "Name is required.");

        if (string.IsNullOrWhiteSpace(Message))
            ModelState.AddModelError(nameof(Message), "Message is required.");

        if (method.Equals("Any", StringComparison.OrdinalIgnoreCase))
        {
            // Require both
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