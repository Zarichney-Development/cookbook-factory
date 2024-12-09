using Cookbook.Factory.Middleware;
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
    IBackgroundTaskQueue taskQueue,
    IRecipeRepository recipeRepository,
    WebScraperService scraperService
) : ControllerBase
{
    private readonly ILogger _log = Log.ForContext<ApiController>();

    [HttpPost("cookbook")]
    [ProducesResponseType(typeof(CookbookOrder), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(BadRequestObjectResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreateCookbook([FromBody] CookbookOrderSubmission submission)
    {
        try
        {
            // reject if no email
            if (string.IsNullOrWhiteSpace(submission.Email))
            {
                _log.Warning("{Method}: No email provided in order", nameof(CreateCookbook));
                return BadRequest("Email is required");
            }

            await emailService.ValidateEmail(submission.Email);
            var order = await orderService.ProcessOrderSubmission(submission);

            // Queue the cookbook generation task
            _ = taskQueue.QueueBackgroundWorkItemAsync(async _ =>
            {
                try
                {
                    await orderService.GenerateCookbookAsync(order, true);
                    await orderService.CompilePdf(order);
                    await orderService.EmailCookbook(order.OrderId);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "{Method}: Background processing failed for order {OrderId}",
                        nameof(CreateCookbook), order.OrderId);
                }
            });

            return Created($"/api/factory/order/{order.OrderId}", order);
        }
        catch (InvalidEmailException ex)
        {
            _log.Warning(ex, "{Method}: Invalid email validation for {Email}",
                nameof(CreateCookbook), submission.Email);
            return BadRequest(new { error = ex.Message, email = ex.Email, reason = ex.Reason.ToString() });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "{Method}: Failed to create cookbook", nameof(CreateCookbook));
            return new ApiErrorResult(ex, $"{nameof(CreateCookbook)}: Failed to create cookbook");
        }
    }

    [HttpGet("order/{orderId}")]
    [ProducesResponseType(typeof(CookbookOrder), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(NotFoundResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetOrder([FromRoute] string orderId)
    {
        try
        {
            var order = await orderService.GetOrder(orderId);
            return Ok(order);
        }
        catch (KeyNotFoundException ex)
        {
            _log.Warning(ex, "{Method}: Order not found: {OrderId}", nameof(GetOrder), orderId);
            return NotFound($"Order not found: {orderId}");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "{Method}: Failed to retrieve order {OrderId}", nameof(GetOrder), orderId);
            return new ApiErrorResult(ex, $"{nameof(GetOrder)}: Failed to retrieve order");
        }
    }

    [HttpPost("order/{orderId}")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(NotFoundResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ReprocessOrder([FromRoute] string orderId)
    {
        try
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
        catch (KeyNotFoundException ex)
        {
            _log.Warning(ex, "{Method}: Order not found for reprocessing: {OrderId}",
                nameof(ReprocessOrder), orderId);
            return NotFound($"Order not found: {orderId}");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "{Method}: Failed to reprocess order {OrderId}",
                nameof(ReprocessOrder), orderId);
            return new ApiErrorResult(ex, $"{nameof(ReprocessOrder)}: Failed to reprocess order");
        }
    }

    [HttpPost("order/{orderId}/pdf")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BadRequestObjectResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(NotFoundResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> RebuildPdf(
        [FromRoute] string orderId,
        [FromQuery] bool email = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(orderId))
            {
                _log.Warning("{Method}: Empty orderId received", nameof(RebuildPdf));
                return BadRequest("OrderId parameter is required");
            }

            var order = await orderService.GetOrder(orderId);

            await orderService.CompilePdf(order, email);

            if (email)
            {
                await orderService.EmailCookbook(order.OrderId);
                return Ok("PDF rebuilt and email sent");
            }

            return Ok("PDF rebuilt");
        }
        catch (KeyNotFoundException ex)
        {
            _log.Warning(ex, "{Method}: Order not found for PDF rebuild: {OrderId}",
                nameof(RebuildPdf), orderId);
            return NotFound($"Order not found: {orderId}");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "{Method}: Failed to rebuild PDF for order {OrderId}",
                nameof(RebuildPdf), orderId);
            return new ApiErrorResult(ex, $"{nameof(RebuildPdf)}: Failed to rebuild PDF");
        }
    }

    [HttpPost("order/{orderId}/email")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(NotFoundResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ResendCookbook([FromRoute] string orderId)
    {
        try
        {
            await orderService.EmailCookbook(orderId);
            return Ok("Email sent");
        }
        catch (KeyNotFoundException ex)
        {
            _log.Warning(ex, "{Method}: Order not found for email resend: {OrderId}",
                nameof(ResendCookbook), orderId);
            return NotFound($"Order not found: {orderId}");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "{Method}: Failed to resend email for order {OrderId}",
                nameof(ResendCookbook), orderId);
            return new ApiErrorResult(ex, $"{nameof(ResendCookbook)}: Failed to resend cookbook email");
        }
    }

    [HttpGet("recipe")]
    [ProducesResponseType(typeof(IEnumerable<Recipe>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BadRequestObjectResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(NotFoundResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetRecipes([FromQuery] string query, [FromQuery] bool scrape = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                _log.Warning("{Method}: Empty query received", nameof(GetRecipes));
                return BadRequest("Query parameter is required");
            }

            var recipes = scrape
                ? await recipeService.GetRecipes(query) // include the feature of replacement name when scraping
                : await recipeService.GetRecipes(query, false);

            if (recipes.ToList().Count == 0)
            {
                return NotFound($"No recipes found for '{query}'");
            }

            return Ok(recipes);
        }
        catch (NoRecipeException e)
        {
            return NotFound(e.Message);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "{Method}: Failed to get recipes for query: {Query}",
                nameof(GetRecipes), query);
            return new ApiErrorResult(ex, $"{nameof(GetRecipes)}: Failed to retrieve recipes");
        }
    }

    [HttpGet("recipe/scrape")]
    [ProducesResponseType(typeof(IEnumerable<Recipe>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BadRequestObjectResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(NotFoundResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ScrapeRecipes(
        [FromQuery] string query,
        [FromQuery] string? site = null,
        [FromQuery] bool? store = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                _log.Warning("{Method}: Empty query received", nameof(ScrapeRecipes));
                return BadRequest("Query parameter is required");
            }

            var recipes = await scraperService.ScrapeForRecipesAsync(query, site);

            if (recipes.ToList().Count == 0)
            {
                return NotFound($"No recipes found for '{query}'");
            }

            if (store != true)
            {
                return Ok(recipes);
            }

            // Further processing for ranking and storing recipes

            var newRecipes =
                await recipeService.RankUnrankedRecipesAsync(
                    recipes.Where(r => !recipeRepository.ContainsRecipe(r.Id!)), query);

            // Process in the background
            _ = recipeRepository.AddUpdateRecipes(newRecipes);

            return Ok(newRecipes);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "{Method}: Failed to scrape recipes for query: {Query}",
                nameof(ScrapeRecipes), query);
            return new ApiErrorResult(ex, $"{nameof(ScrapeRecipes)}: Failed to scrape recipes");
        }
    }

    [HttpPost("email/validate")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BadRequestObjectResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ValidateEmail([FromQuery] string email)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                _log.Warning("{Method}: Empty email received", nameof(ValidateEmail));
                return BadRequest("Email parameter is required");
            }

            await emailService.ValidateEmail(email);
            return Ok("Valid");
        }
        catch (InvalidEmailException ex)
        {
            _log.Warning(ex, "{Method}: Invalid email validation for {Email}",
                nameof(ValidateEmail), email);
            return BadRequest(new
            {
                error = ex.Message,
                email = ex.Email,
                reason = ex.Reason.ToString()
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "{Method}: Failed to validate email: {Email}",
                nameof(ValidateEmail), email);
            return new ApiErrorResult(ex, $"{nameof(ValidateEmail)}: Failed to validate email");
        }
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