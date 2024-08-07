using Cookbook.Factory.Models;
using Cookbook.Factory.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Cookbook.Factory.Controllers;

[ApiController]
[Route("api")]
public class ApiController(
    RecipeService recipeService,
    OrderService orderService,
    IEmailService emailService
) : ControllerBase
{
    private readonly ILogger _log = Log.ForContext<ApiController>();


    [HttpPost("email")]
    public async Task<IActionResult> SendCookbook()
    {
        var templateData = new Dictionary<string, object>
        {
            { "title", "Your Cookbook is Ready!" },
            { "company_name", "Cookbook Factory" },
            { "current_year", DateTime.Now.Year },
            { "unsubscribe_link", "https://cookbookfactory.com/unsubscribe" },
        };

        await emailService.SendEmail(
            "zarichney@gmail.com",
            "Your Cookbook is Ready!",
            "cookbook-ready",
            templateData
        );

        return Ok("Email sent");
    }


    [HttpPost("cookbook")]
    public async Task<ActionResult<CookbookOrder>> CreateCookbook([FromBody] CookbookOrderSubmission submission)
    {
        // reject if no email
        if (string.IsNullOrWhiteSpace(submission.Email))
        {
            _log.Warning("No email provided in order");
            return BadRequest("Email is required");
        }

        var order = await orderService.ProcessOrderSubmission(submission);

        await orderService.GenerateCookbookAsync(order, true);

        orderService.CompilePdf(order);

        return Ok(order);
    }


    [HttpGet("recipe")]
    public async Task<IActionResult> GetRecipes([FromQuery] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _log.Warning("Empty query received");
            return BadRequest("Query parameter is required");
        }

        try
        {
            var recipes = await recipeService.GetRecipes(query);

            if (recipes.Count == 0)
            {
                return NotFound($"No recipes found for '{query}'");
            }

            return Ok(recipes);
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"Error occurred while processing query: {query}");
            return StatusCode(500, ex);
        }
    }

    [HttpGet("pdf")]
    public async Task<IActionResult> GetPdf([FromQuery] string orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            _log.Warning("Empty orderId received");
            return BadRequest("OrderId parameter is required");
        }

        var order = await orderService.GetOrder(orderId);

        orderService.CompilePdf(order);

        return Ok();
    }
}