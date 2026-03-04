using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace ITWebsite.Pages
{
    [DisableRateLimiting]
    public class StatusModel : PageModel
    {
        [BindProperty(SupportsGet = true)]
        public int Code { get; set; }

        public void OnGet() { }
    }
}