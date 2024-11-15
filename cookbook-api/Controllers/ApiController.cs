using Cookbook.Factory.Models;
using Cookbook.Factory.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Cookbook.Factory.Controllers;

[ApiController]
[Route("api/factory")]
public class ApiController(
    RecipeService recipeService,
    OrderService orderService,
    IEmailService emailService,
    IBackgroundTaskQueue taskQueue
) : ControllerBase
{
    private readonly ILogger _log = Log.ForContext<ApiController>();

    [HttpPost("cookbook")]
    public async Task<ActionResult<CookbookOrder>> CreateCookbook([FromBody] CookbookOrderSubmission submission)
    {
        // reject if no email
        if (string.IsNullOrWhiteSpace(submission.Email))
        {
            _log.Warning("No email provided in order");
            return BadRequest("Email is required");
        }

        try
        {
            await emailService.ValidateEmail(submission.Email);
        }
        catch (InvalidEmailException e)
        {
            return BadRequest(new
            {
                error = e.Message,
                email = e.Email,
                reason = e.Reason.ToString()
            });
        }

        var order = await orderService.ProcessOrderSubmission(submission);

        // Queue the cookbook generation task
        _ = taskQueue.QueueBackgroundWorkItemAsync(async _ =>
        {
            await orderService.GenerateCookbookAsync(order, true);
            await orderService.CompilePdf(order);
            await orderService.EmailCookbook(order.OrderId);
        });

        return Created($"/api/factory/order/{order.OrderId}", order);
    }

    [HttpGet("order/{orderId}")]
    public async Task<IActionResult> GetOrder([FromRoute] string orderId)
    {
        try
        {
            var order = await orderService.GetOrder(orderId);
            return Ok(order);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Order not found using orderId: {orderId}", orderId);
            return NotFound($"Order not found: {orderId}");
        }
    }

    [HttpPost("order/{orderId}")]
    public async Task<IActionResult> ReprocessOrder([FromRoute] string orderId)
    {
        var order = await orderService.GetOrder(orderId);

        // Queue the cookbook generation task
        _ = taskQueue.QueueBackgroundWorkItemAsync(async _ =>
        {
            await orderService.GenerateCookbookAsync(order, true);
            await orderService.CompilePdf(order);
            await orderService.EmailCookbook(order.OrderId);
        });

        return Ok("Reprocessing order");
    }

    [HttpPost("order/{orderId}/pdf")]
    public async Task<IActionResult> RebuildPdf([FromRoute] string orderId, [FromQuery] bool email = false)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            _log.Warning("Empty orderId received");
            return BadRequest("OrderId parameter is required");
        }

        var order = await orderService.GetOrder(orderId);

        await orderService.CompilePdf(order, email);

        var response = "PDF Rebuilt";

        if (email)
        {
            await orderService.EmailCookbook(order.OrderId);
            response += " and email sent";
        }

        return Ok(response);
    }

    [HttpPost("order/{orderId}/email")]
    public async Task<IActionResult> ResendCookbook([FromRoute] string orderId)
    {
        await orderService.EmailCookbook(orderId);

        return Ok("Email sent");
    }

    [HttpGet("recipe")]
    public async Task<IActionResult> GetRecipes([FromQuery] string? query, [FromQuery] bool scrape = false,
        [FromQuery] string? site = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _log.Warning("Empty query received");
            return BadRequest("Query parameter is required");
        }

        try
        {
            var recipes = await recipeService.GetRecipes(query, null, scrape);

            if (recipes.ToList().Count == 0)
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

    [HttpPost("email/validate")]
    public async Task<IActionResult> ValidateEmail([FromQuery] string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            _log.Warning("Empty email received");
            return BadRequest("Email parameter is required");
        }

        try
        {
            await emailService.ValidateEmail(email);
        }
        catch (InvalidEmailException e)
        {
            return BadRequest(new
            {
                error = e.Message,
                email = e.Email,
                reason = e.Reason.ToString()
            });
        }

        return Ok("Valid");
    }

    [HttpGet("health/secure")]
    public IActionResult HealthCheck()
    {
        return Ok(new
        {
            Success = true,
            Time = DateTime.Now.ToLocalTime()
        });
    }
}

[ApiController]
[Route("api/factory")]
public class PublicController : ControllerBase
{
    [HttpGet("health")]
    public IActionResult HealthCheck()
    {
        return Ok(new
        {
            Success = true,
            Time = DateTime.Now.ToLocalTime()
        });
    }
}