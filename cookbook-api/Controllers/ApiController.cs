using Cookbook.Factory.Models;
using Cookbook.Factory.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cookbook.Factory.Controllers;

[ApiController]
[Route("api")]
public class ApiController : ControllerBase
{
    private readonly ILogger<ApiController> _logger;
    private readonly RecipeService _recipeService;

    public ApiController(ILogger<ApiController> logger, RecipeService recipeService)
    {
        _logger = logger;
        _recipeService = recipeService;
    }

    [HttpPost("cookbook")]
    public async Task<IActionResult> CreateCookbook([FromBody] CookbookOrder order)
    {
        // reject if no email
        if (string.IsNullOrWhiteSpace(order.UserDetails.Email))
        {
            _logger.LogWarning("No email provided in order");
            return BadRequest("Email is required");
        }
        
        var recipes = await _recipeService.GetRecipes("Pad Thai");
        
        var result = await _recipeService.CurateRecipe(recipes, order);
        
        return Ok(result);
    }


    [HttpGet("recipe")]
    public async Task<IActionResult> GetRecipes([FromQuery] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _logger.LogWarning("Empty query received");
            return BadRequest("Query parameter is required");
        }

        try
        {
            var recipes = await _recipeService.GetRecipes(query);

            if (recipes.Count == 0)
            {
                return NotFound($"No recipes found for '{query}'");
            }

            return Ok(recipes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error occurred while processing query: {query}");
            return StatusCode(500, "An error occurred while processing your request");
        }
    }
}
