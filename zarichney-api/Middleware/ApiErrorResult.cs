using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Zarichney.Middleware;

public class ApiErrorResult(
  Exception exception,
  string userMessage = "An unexpected error occurred",
  HttpStatusCode statusCode = HttpStatusCode.InternalServerError)
  : IActionResult
{
  public async Task ExecuteResultAsync(ActionContext context)
  {
    var response = new
    {
      Error = new
      {
        Message = userMessage,
        Type = exception.GetType().Name,
        Details = exception.Message,
        exception.Source,
        StackTrace = exception.StackTrace?.Split(Environment.NewLine)
          .Select(line => line.TrimStart())
          .ToList(),
      },
      Request = new
      {
        Path = context.HttpContext.Request.Path.Value,
        context.HttpContext.Request.Method,
        Controller = context.ActionDescriptor.DisplayName
      },
      TraceId = context.HttpContext.TraceIdentifier,
      InnerException = exception.InnerException == null
        ? null
        : new
        {
          exception.InnerException.Message,
          Type = exception.InnerException.GetType().Name,
          StackTrace = exception.InnerException.StackTrace?.Split(Environment.NewLine)
            .Select(line => line.TrimStart())
            .ToList()
        }
    };

    context.HttpContext.Response.StatusCode = (int)statusCode;
    context.HttpContext.Response.ContentType = "application/json";

    await context.HttpContext.Response.WriteAsJsonAsync(response, new JsonSerializerOptions
    {
      WriteIndented = true,
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });
  }
}