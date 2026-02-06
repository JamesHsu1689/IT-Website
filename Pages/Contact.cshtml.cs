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
    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Message { get; set; } = "";
    [BindProperty] public string ContactMethod { get; set; } = "";

    public bool Sent { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();

        try
        {
            await SendEmailAsync();
            Sent = true;
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", $"Email failed: {ex.Message}");
        }

        return Page();
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
}