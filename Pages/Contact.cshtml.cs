using System.Net;
using System.Net.Mail;
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
            // Optional: add a friendly error
            ModelState.AddModelError("", $"Email failed: {ex.Message}");
        }

        return Page();
    }


    /* 
    Currently not working. Catch message:

    Email failed: The SMTP server requires a secure connection or the client was not authenticated. 
    The server response was: 5.7.57 Client not authenticated to send mail. 
    Error: 535 5.7.139 Authentication unsuccessful, SmtpClientAuthentication is disabled for the Mailbox. 
    Visit https://aka.ms/smtp_auth_disabled for more information. 
    // [MW4P223CA0015.NAMP223.PROD.OUTLOOK.COM 2026-02-06T00:38:24.198Z 08DE64628E67C48C]
    
     */
    private async Task SendEmailAsync()
    {
        var smtpServer = _config["EmailSettings:SmtpServer"];
        var username   = _config["EmailSettings:Username"];
        var password   = _config["EmailSettings:Password"];
        var toEmail    = _config["EmailSettings:ToEmail"] ?? username;
        var port       = int.Parse(_config["EmailSettings:Port"] ?? "587");

        using var smtp = new SmtpClient(smtpServer, port)
        {
            EnableSsl = true, // STARTTLS on 587
            Credentials = new NetworkCredential(username, password)
        };

        using var mail = new MailMessage
        {
            From = new MailAddress(username),
            Subject = "New Support Request",
            Body =
$"""
Name: {Name}
Email: {Email}
Preferred Contact: {ContactMethod}

Message:
{Message}
"""
        };

        mail.To.Add(toEmail);

        await smtp.SendMailAsync(mail);
    }
}