using System.Collections.Immutable;

namespace Zarichney.Config;

public class ApiKeyConfig
{
  public string AllowedKeys { get; set; } = string.Empty;

  private ImmutableHashSet<string>? _validApiKeys;

  public ImmutableHashSet<string> ValidApiKeys
  {
    get
    {
      return _validApiKeys ??= AllowedKeys
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToImmutableHashSet();
    }
  }
}