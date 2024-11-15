using System.Text.Json;
using Cookbook.Factory.Services;

namespace Cookbook.Factory.Prompts;

public class ChooseRecipesPrompt : PromptBase
{
    public override string Name => "Choose Recipes";
    public override string Description => "Select the most relevant recipe URLs from search results";
    public override string Model => LlmModels.Gpt4Omini;
    public override string SystemPrompt => 
        """
        You are an AI assistant specialized in selecting the most relevant recipe URLs from search results. Your task is to choose the most relevant recipes that best match the given query. Follow these guidelines:
        1. Analyze the query carefully to understand the recipe requirements.
        2. Evaluate each URL for relevance to the query, considering any available context in the URL.
        3. If URLs contain meaningful information, prioritize those that seem most relevant to the query and filter out any irrelevant URLs.
        4. If URLs do not contain meaningful context (e.g., only recipe IDs), select the first n URLs as specified in the user prompt.
        5. The number of URLs to select will be specified in the user prompt.
        6. Return only the indices of the selected URLs, up to the requested amount.
        Your response should be a list of indices corresponding to the selected URLs.
        """;

    public string GetUserPrompt(string? query, List<string> urls, int count)
    {
        var urlList = string.Join("\n", urls.Select((url, index) => $"{index + 1}. {url}"));
        return $"""
                Query: '{query}'

                URLs:
                {urlList}

                Select the top {count} most relevant URLs and exclude anything seemingly irrelevant.
                """;
    }

    public override FunctionDefinition GetFunction() => new()
    {
        Name = "SelectTopRecipes",
        Description = "Select the indices of the most relevant recipe URLs from search results",
        Strict = true,
        Parameters = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                selectedIndices = new
                {
                    type = "array",
                    items = new { type = "integer" },
                    description = "Indices of the selected URLs ranked by relevancy"
                }
            },
            required = new[] { "selectedIndices" }
        })
    };
}

public class SearchResult
{
    public required List<int> SelectedIndices { get; set; }
}