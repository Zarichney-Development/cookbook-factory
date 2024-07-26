using Cookbook.Factory.Models;
using Cookbook.Factory.Services;
using Microsoft.AspNetCore.Mvc;
using ILogger = Cookbook.Factory.Logging.ILogger;

namespace Cookbook.Factory.Controllers;

[ApiController]
[Route("api")]
public class ApiController(ILogger logger, RecipeService recipeService) : ControllerBase
{
    [HttpPost("cookbook")]
    public async Task<IActionResult> CreateCookbook([FromBody] CookbookOrder order)
    {
        // reject if no email
        if (string.IsNullOrWhiteSpace(order.Email))
        {
            logger.LogWarning("No email provided in order");
            return BadRequest("Email is required");
        }

        try
        {
            var query = "buffalo ranch dip";
            
            var recipes = await recipeService.GetRecipes(query);

            var result = await recipeService.SynthesizeRecipe(recipes, order, query);
            
            return Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error occurred while processing order");
            return StatusCode(500, ex);
        }

    }


    [HttpGet("recipe")]
    public async Task<IActionResult> GetRecipes([FromQuery] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            logger.LogWarning("Empty query received");
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
            logger.LogError(ex, $"Error occurred while processing query: {query}");
            return StatusCode(500, ex);
        }
    }
}