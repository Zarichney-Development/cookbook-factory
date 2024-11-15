using AutoMapper;
using OpenAI.Assistants;

namespace Cookbook.Factory.Prompts;

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
            .ForMember(dest => dest.Parameters, opt => opt.MapFrom(src => BinaryData.FromString(src.Parameters)));

        CreateMap<FunctionToolDefinition, FunctionDefinition>()
            .ForMember(dest => dest.Name, opt => opt.MapFrom(src => src.FunctionName))
            .ForMember(dest => dest.Description, opt => opt.MapFrom(src => src.Description))
            .ForMember(dest => dest.Parameters, opt => opt.MapFrom(src => src.Parameters.ToString()));
    }
}