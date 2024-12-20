using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Zarichney.Middleware;

public class SwaggerSecuritySchemeFilter : IOperationFilter
{
  public void Apply(OpenApiOperation operation, OperationFilterContext context)
  {
    // Check if the endpoint is the health check
    var isHealthCheck = context.ApiDescription.RelativePath?.Contains(
      "health",
      StringComparison.OrdinalIgnoreCase) ?? false;

    if (isHealthCheck)
    {
      // Remove security requirement for health check endpoint
      operation.Security.Clear();
    }
  }
}