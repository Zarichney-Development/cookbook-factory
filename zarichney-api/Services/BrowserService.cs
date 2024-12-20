using Microsoft.Playwright;
using Serilog;
using Zarichney.Cookbook.Services;
using ILogger = Serilog.ILogger;

namespace Zarichney.Services;

public interface IBrowserService
{
  Task<List<string>> GetContentAsync(string url, string selector, CancellationToken cancellationToken = default);
}

public class BrowserService : IBrowserService, IAsyncDisposable
{
  private readonly ILogger _log = Log.ForContext<BrowserService>();
  private readonly SemaphoreSlim _semaphore;
  private readonly WebscraperConfig _config;
  private readonly IBrowser? _browser;
  private readonly IPlaywright? _playwright;
  private bool _disposed;

  private readonly BrowserNewContextOptions _contextConfig = new()
  {
    AcceptDownloads = false,
    BypassCSP = true,
    JavaScriptEnabled = true,
    IgnoreHTTPSErrors = true,
    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko)",
    ViewportSize = new ViewportSize { Width = 1280, Height = 720 }
  };

  private readonly BrowserTypeLaunchOptions _browserOptions = new()
  {
    Headless = true,
    Timeout = 10000,
    Args =
    [
      "--no-sandbox",
      "--disable-setuid-sandbox",
      "--disable-gpu",
      "--disable-extensions",
      "--disable-component-update",
      "--disable-background-networking",
      "--disable-default-apps",
      "--disable-sync",
      "--disable-translate",
      "--disable-notifications",
      "--disable-background-timer-throttling",
      "--disable-renderer-backgrounding",
      "--disable-backgrounding-occluded-windows",
      "--disable-breakpad",
      "--disable-client-side-phishing-detection",
      "--disable-ipc-flooding-protection",
      "--disable-gpu-compositing",
      "--disable-accelerated-2d-canvas",
      "--disable-accelerated-video-decode",
      "--mute-audio",
      "--disable-logging",
      "--js-flags=--max-old-space-size=512"
    ]
  };

  public BrowserService(WebscraperConfig config, IWebHostEnvironment env)
  {
    _config = config;
    _semaphore = new SemaphoreSlim(config.MaxParallelPages, config.MaxParallelPages);

    if (env.IsProduction())
    {
      _browserOptions.Channel = "chrome";
      _browserOptions.ExecutablePath = "/usr/bin/google-chrome";
    }

    _playwright = Playwright.CreateAsync().GetAwaiter().GetResult();
    _browser = _playwright.Chromium.LaunchAsync(_browserOptions).GetAwaiter().GetResult();
    _browser.Disconnected += Browser_Disconnect;
  }

  public async Task<List<string>> GetContentAsync(string url, string selector,
    CancellationToken cancellationToken = default)
  {
    await _semaphore.WaitAsync(cancellationToken);
    IBrowserContext? context = null;
    IPage? page = null;
    try
    {
      context = await _browser!.NewContextAsync(_contextConfig);
      context.SetDefaultTimeout(_config.MaxWaitTimeMs);
      context.SetDefaultNavigationTimeout(_config.MaxWaitTimeMs);

      page = await context.NewPageAsync();
      page.Console += Page_Console;
      page.PageError += Page_PageError;

      _log.Debug("Navigating to URL: {url}", url);

      var response = await page.GotoAsync(url, new PageGotoOptions
      {
        WaitUntil = WaitUntilState.DOMContentLoaded,
        Timeout = _config.MaxWaitTimeMs
      });

      if (!(response?.Ok ?? false))
      {
        _log.Warning("Failed to load page. Status: {status}", response?.Status);
        return [];
      }

      // Wait a short moment for initial JavaScript to execute
      await page.WaitForTimeoutAsync(100);

      // Handle cookie consent banner if present
      try
      {
        var consentButton = await page.QuerySelectorAsync("button#cookie-consent-accept");
        if (consentButton != null)
        {
          await consentButton.ClickAsync();
          _log.Information("Accepted cookie consent.");
        }
      }
      catch (Exception ex)
      {
        _log.Debug(ex, "No cookie consent banner found or failed to click.");
      }

      // Simulate mouse movement to the center of the page
      var centerX = (page.ViewportSize?.Width ?? 1280) / 2;
      var centerY = (page.ViewportSize?.Height ?? 720) / 2;
      await page.Mouse.MoveAsync(centerX, centerY);
      _log.Information("Simulated mouse movement to position ({x}, {y})", centerX, centerY);

      // Wait for the selector to appear
      try
      {
        _log.Debug("Waiting for selector: {selector}", selector);

        await page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions
        {
          Timeout = _config.MaxWaitTimeMs
        });
      }
      catch (TimeoutException)
      {
        _log.Warning("Selector '{selector}' not found within timeout.", selector);
        return [];
      }

      var elements = await page.QuerySelectorAllAsync(selector);

      var content = new List<string>();

      foreach (var element in elements)
      {
        var href = await element.GetAttributeAsync("href");
        if (!string.IsNullOrEmpty(href))
        {
          content.Add(href);
        }
      }

      _log.Debug("Retrieved {count} items from URL: {url}", content.Count, url);

      return content.Distinct().ToList();
    }
    catch (Exception ex)
    {
      _log.Error(ex, "Error occurred while getting content from URL: {url}", url);
      return new List<string>();
    }
    finally
    {
      if (page != null)
      {
        page.Console -= Page_Console;
        page.PageError -= Page_PageError;
        try
        {
          await page.CloseAsync();
        }
        catch (Exception ex)
        {
          _log.Error(ex, "Error occurred while closing the page.");
        }
      }

      if (context != null)
      {
        try
        {
          await context.CloseAsync();
        }
        catch (Exception ex)
        {
          _log.Error(ex, "Error occurred while closing the browser context.");
        }
      }

      _semaphore.Release();
    }
  }

  private void Page_PageError(object? sender, object e)
  {
    _log.Warning("Page error: {@error}", e);
  }

  private void Page_Console(object? sender, IConsoleMessage msg)
  {
    _log.Debug("Console message: {type} - {text}", msg.Type, msg.Text);
  }

  private void Browser_Disconnect(object? sender, IBrowser e)
  {
    _log.Error("Browser disconnected. Sender: {sender}", sender);
  }

  public async ValueTask DisposeAsync()
  {
    if (!_disposed)
    {
      if (_browser != null)
      {
        _browser.Disconnected -= Browser_Disconnect;
        await _browser.CloseAsync();
      }

      _playwright?.Dispose();
      _semaphore.Dispose();
      _disposed = true;
    }
  }
}