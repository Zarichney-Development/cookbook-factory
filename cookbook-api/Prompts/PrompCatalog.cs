using System.Text.Json;
using AutoMapper;
using Cookbook.Factory.Models;

namespace Cookbook.Factory.Prompts;

public static class PrompCatalog
{
    public static class RankRecipe
    {
        public const string SystemPrompt =
            "You are a specialist in assessing the relevancy of a recipe. Your task is to provide a relevancy score from 0 to 100 on how relevant a recipe is to a given query.";

        public static string UserPrompt(Recipe recipe, string query)
            => $"Query: '{query}'\n\nRecipe data:\n{JsonSerializer.Serialize(recipe)}";

        public static (string name, string description, string parameters) GetFunction()
            => (Function.Name, Function.Description, Function.Parameters);

        private static class Function
        {
            public const string Name = "RankRecipe";
            public const string Description = "Assess the relevancy of a recipe based on a given query";

            public static readonly string Parameters = JsonSerializer.Serialize(new
            {
                type = "object",
                properties = new
                {
                    relevancyScore = new
                    {
                        type = "integer",
                        description = "A score from 0 to 100 indicating how relevant the recipe is to the given query"
                    },
                    relevancyReasoning = new
                        { type = "string", description = "A brief explanation of the relevancy decision" },
                },
                required = new[] { "relevancyScore", "relevancyReasoning" }
            });
        }
    }

    public static class CleanRecipe
    {
        public const string SystemPrompt =
            """
            You are specialized in cleaning and standardizing scraped recipe data. Follow these guidelines:
            1. Standardize units of measurement (e.g., 'tbsp', 'tsp', 'g').
            2. Correct spacing and spelling issues.
            3. Format ingredients and directions as arrays of strings.
            4. Standardize cooking times and temperatures (e.g., '350°F', '175°C').
            5. Remove irrelevant or accidental content.
            6. Do not add new information or change the original recipe.
            7. Leave empty fields as they are, replace nulls with empty strings.
            8. Ensure consistent formatting.
            Return a cleaned, standardized version of the recipe data, preserving original structure and information while improving clarity and consistency.
            """;

        public static string UserPrompt(IMapper mapper, Recipe recipe)
            => JsonSerializer.Serialize(mapper.Map<ScrapedRecipe>(recipe));

        public static (string name, string description, string parameters) GetFunction()
            => (Function.Name, Function.Description, Function.Parameters);

        private static class Function
        {
            public const string Name = "CleanRecipeData";
            public const string Description = "Clean and standardize scraped recipe data";

            public static readonly string Parameters = JsonSerializer.Serialize(new
            {
                type = "object",
                properties = new
                {
                    title = new { type = "string" },
                    description = new { type = "string" },
                    servings = new { type = "string" },
                    prepTime = new { type = "string" },
                    cookTime = new { type = "string" },
                    totalTime = new { type = "string" },
                    notes = new { type = "string" },
                    ingredients = new { type = "array", items = new { type = "string" } },
                    directions = new { type = "array", items = new { type = "string" } },
                },
                required = new[]
                {
                    "title", "servings", "description", "prepTime", "cookTime", "totalTime", "notes", "ingredients",
                    "directions"
                }
            });
        }
    }

    public static class SynthesizeRecipe
    {
        public const string SystemPrompt =
            """
            # Recipe Curation System Prompt

            **Role:** AI assistant for personalized recipe creation.

            **Steps:**
            1. **Analyze Cookbook Order:**
               - Review Cookbook Content, Details, and User Details.
               - Focus on dietary restrictions, allergies, skill level, and cooking goals.
            2. **Evaluate Provided Recipes:**
               - Analyze recipes from various sources for inspiration.
            3. **Create New Recipe:**
               - Blend elements from relevant recipes.
               - Ensure it meets dietary needs, preferences, skill level, and time constraints.
               - Align with the cookbook's theme and cultural goals.
            4. **Customize Recipe:**
               - Modify ingredients and techniques for dietary restrictions and allergies.
               - Adjust serving size.
               - Include alternatives or substitutions.
            5. **Enhance Recipe:**
               - Add cultural context or storytelling.
               - Incorporate educational content.
               - Include meal prep tips or leftover ideas.
            6. **Format Output:**
               - Use provided Recipe class structure.
               - Fill out all fields.
               - Include customizations, cultural context, or educational content in Notes.
            7. **Provide Attribution:**
               - List "Inspired by" URLs from original recipes.
               - Only include those that contributed towards the synthesized recipe.

            **Output Format:**
            ```json
            {
                'title': '',
                'description': '',
                'servings': '',
                'prepTime': '',
                'cookTime': '',
                'totalTime': '',
                'ingredients': [],
                'directions': [],
                'notes': '',
                'inspiredBy': ['list-of-urls']
            }
            ```

            **Goal:** Tailor recipes to user needs and preferences while maintaining original integrity and cookbook theme.
            """;

        public static string UserPrompt(List<Recipe> recipes, CookbookOrder order)
            => $"""
                # Recipe data:
                ```json
                {JsonSerializer.Serialize(recipes)}
                ```

                # Cookbook Order:
                {order.ToMarkdown()}

                Please synthesize a personalized recipe.
                """;

        public static (string name, string description, string parameters) GetFunction()
            => (Function.Name, Function.Description, Function.Parameters);

        public static class Function
        {
            public const string Name = "SynthesizeRecipe";

            public const string Description =
                "Refer to existing recipes and synthesize a new one based on user expectations";

            public static readonly string Parameters = JsonSerializer.Serialize(new
            {
                type = "object",
                properties = new
                {
                    title = new { type = "string" },
                    description = new { type = "string" },
                    servings = new { type = "string" },
                    prepTime = new { type = "string" },
                    cookTime = new { type = "string" },
                    totalTime = new { type = "string" },
                    ingredients = new { type = "array", items = new { type = "string" } },
                    directions = new { type = "array", items = new { type = "string" } },
                    notes = new { type = "string" },
                    inspiredBy = new { type = "array", items = new { type = "string" } }
                },
                required = new[]
                {
                    "title", "ingredients", "directions", "servings", "description",
                    "prepTime", "cookTime", "totalTime", "notes", "inspiredBy"
                }
            });
        }
    }

    public static class AnalyzeRecipe
    {
        public const string SystemPrompt =
            """
            # Recipe Analysis System Prompt

            You are an AI assistant specialized in recipe analysis and quality assurance. Your task is to thoroughly review a curated recipe and ensure it meets the user's specifications as outlined in their cookbook order. Follow these steps:

            1. Carefully review the provided recipe and cookbook order details.

            2. Verify that the recipe adheres to:
               - Dietary restrictions and allergies
               - Skill level appropriateness
               - Cooking goals and preferences
               - Cultural exploration aims
               - Cookbook theme and style

            3. Assess the recipe for:
               - Completeness and clarity of instructions
               - Appropriate serving size
               - Realistic prep and cook times
               - Inclusion of required elements (e.g., cultural context, educational content, practical tips)

            4. Determine if the recipe passes the quality assurance check:
               - If it passes, provide a brief justification highlighting its strengths.
               - If it doesn't pass, offer specific critiques and suggestions for improvement.

            5. Call the AnalyzeRecipe function with your assessment, including a boolean 'passes' flag and your detailed feedback.
            Wait for a new recipe to be provided via tool output, then analyze it again.
            Repeat this process until a recipe passes your quality check.
            """;

        public static string UserPrompt(SynthesizedRecipe curatedRecipe, CookbookOrder order)
            => $"""
                # Recipe data:
                ```json
                {JsonSerializer.Serialize(curatedRecipe)}
                ```

                # Cookbook Order:
                {order.ToMarkdown()}

                Please analyze the recipe and provide feedback on its quality and relevancy to the given cookbook order.
                """;


        public static (string name, string description, string parameters) GetFunction()
            => (Function.Name, Function.Description, Function.Parameters);

        public static class Function
        {
            public const string Name = "AnalyzeRecipe";
            public const string Description = "Analyze the quality and relevancy of a recipe";

            public static readonly string Parameters = JsonSerializer.Serialize(new
            {
                type = "object",
                properties = new
                {
                    passes = new { type = "boolean" },
                    feedback = new { type = "string" }
                },
                required = new[] { "passes", "feedback" }
            });
        }
    }
}