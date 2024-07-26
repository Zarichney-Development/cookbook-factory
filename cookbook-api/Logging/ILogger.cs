namespace Cookbook.Factory.Logging;

public interface ILogger
{
    void LogVerbose(string messageTemplate, params object[] propertyValues);
    void LogDebug(string messageTemplate, params object[] propertyValues);
    void LogInformation(string messageTemplate, params object[] propertyValues);
    void LogInformation(Exception exception, string messageTemplate, params object?[]? propertyValues);
    void LogWarning(string messageTemplate, params object[] propertyValues);
    void LogWarning(Exception exception, string messageTemplate, params object[] propertyValues);
    void LogError(string messageTemplate, params object?[]? propertyValues);
    void LogError(Exception exception, string messageTemplate, params object?[]? propertyValues);
    void LogFatal(string messageTemplate, params object[] propertyValues);
}

public class Logger(Serilog.ILogger logger) : ILogger
{
    private readonly Serilog.ILogger _logger = logger.ForContext<Logger>();

    public void LogVerbose(string messageTemplate, params object[] propertyValues)
        => _logger.Verbose(messageTemplate, propertyValues);

    public void LogDebug(string messageTemplate, params object[] propertyValues)
        => _logger.Debug(messageTemplate, propertyValues);

    public void LogInformation(string messageTemplate, params object[] propertyValues)
        => _logger.Information(messageTemplate, propertyValues);

    public void LogInformation(Exception exception, string messageTemplate, params object?[]? propertyValues)
        => _logger.Information(exception, messageTemplate, propertyValues);

    public void LogWarning(string messageTemplate, params object[] propertyValues)
        => _logger.Warning(messageTemplate, propertyValues);

    public void LogWarning(Exception exception, string messageTemplate, params object[] propertyValues)
        => _logger.Warning(exception, messageTemplate, propertyValues);

    public void LogError(string messageTemplate, params object?[]? propertyValues)
        => _logger.Error(messageTemplate, propertyValues);

    public void LogError(Exception exception, string messageTemplate, params object?[]? propertyValues)
        => _logger.Error(exception, messageTemplate, propertyValues);

    public void LogFatal(string messageTemplate, params object[] propertyValues)
        => _logger.Fatal(messageTemplate, propertyValues);
}