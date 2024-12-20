using System.Text.Json;
using System.Text.Json.Nodes;
using AutoMapper;
using OpenAI.Assistants;

namespace Zarichney.Config;

public abstract class PromptBase
{
  public abstract string Name { get; }
  public abstract string Description { get; }
  public abstract string SystemPrompt { get; }
  public abstract string? Model { get; }
  public abstract FunctionDefinition GetFunction();
}

public class FunctionDefinition
{
  public required string Name { get; set; }
  public required string Description { get; set; }
  public required string Parameters { get; set; }
  public required bool Strict = true;
}

public class FunctionDefinitionMappingProfile : Profile
{
  public FunctionDefinitionMappingProfile()
  {
    CreateMap<FunctionDefinition, FunctionToolDefinition>()
      .ForMember(dest => dest.FunctionName, opt => opt.MapFrom(src => src.Name))
      .ForMember(dest => dest.Description, opt => opt.MapFrom(src => src.Description))
      .ForMember(dest => dest.Parameters, opt => opt.MapFrom(src => AddAdditionalProperties(src.Parameters)));


    CreateMap<FunctionToolDefinition, FunctionDefinition>()
      .ForMember(dest => dest.Name, opt => opt.MapFrom(src => src.FunctionName))
      .ForMember(dest => dest.Description, opt => opt.MapFrom(src => src.Description))
      .ForMember(dest => dest.Parameters, opt => opt.MapFrom(src => src.Parameters.ToString()));
  }

  private static BinaryData AddAdditionalProperties(string parametersJson)
  {
    try
    {
      // Parse the JSON into a dynamic object
      var jsonNode = JsonNode.Parse(parametersJson);

      if (jsonNode is JsonObject jsonObject)
      {
        // Check if "additionalProperties" already exists
        if (!jsonObject.ContainsKey("additionalProperties"))
        {
          // Add "additionalProperties": false
          jsonObject["additionalProperties"] = false;
        }
      }

      // Serialize the modified JSON back to a string
      var updatedJson = jsonNode?.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

      return BinaryData.FromString(updatedJson ?? parametersJson);
    }
    catch (JsonException)
    {
      // If parsing fails, return the original parameters (fallback)
      return BinaryData.FromString(parametersJson);
    }
  }
}