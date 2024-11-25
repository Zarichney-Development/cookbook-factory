using Microsoft.Playwright;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Cookbook.Factory.Services;

public interface IBrowserService
{
    Task<List<string>> GetContentAsync(string url, string selector, CancellationToken cancellationToken = default);
}

public class BrowserService : IBrowserService, IAsyncDisposable
{
    private readonly ILogger _log = Log.ForContext<BrowserService>();
    private readonly SemaphoreSlim _semaphore;
    private readonly WebscraperConfig _config;
    private readonly IBrowser _browser;
    private readonly IPlaywright _playwright;
    private bool _disposed;

    public BrowserService(WebscraperConfig config, IWebHostEnvironment env)
    {
        _config = config;
        _semaphore = new SemaphoreSlim(config.MaxParallelPages, config.MaxParallelPages);

        _playwright = Playwright.CreateAsync().GetAwaiter().GetResult();

        var browserOptions = new BrowserTypeLaunchOptions
        {
            Headless = true,
            Timeout = config.MaxWaitTimeMs,
            Args = GetBrowserArgs(),
            HandleSIGINT = true,
            HandleSIGTERM = true,
            HandleSIGHUP = true
        };

        if (env.IsProduction())
        {
            browserOptions.Channel = "chrome";
            browserOptions.ExecutablePath = "/usr/bin/google-chrome";
        }

        _browser = _playwright.Chromium.LaunchAsync(browserOptions).GetAwaiter().GetResult();
    }

    private static string[] GetBrowserArgs()
    {
        var args = new List<string>
        {
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
            "--disable-software-rasterizer",
            "--mute-audio",
            "--disable-logging",
            "--js-flags=--max-old-space-size=128"
        };

        return args.ToArray();
    }

    public async Task<List<string>> GetContentAsync(string url, string selector,
        CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);

        try
        {
            await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                AcceptDownloads = false,
                BypassCSP = true,
                JavaScriptEnabled = true,
                IgnoreHTTPSErrors = true,
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko)",
                ViewportSize = new ViewportSize { Width = 1280, Height = 720 }
            });

            context.SetDefaultTimeout(_config.MaxWaitTimeMs);
            context.SetDefaultNavigationTimeout(_config.MaxWaitTimeMs);

            var page = await context.NewPageAsync();

            // Log console messages and page errors
            page.Console += (_, msg) => { _log.Debug("Console message: {type} - {text}", msg.Type, msg.Text); };
            page.PageError += (_, error) => { _log.Warning("Page error: {error}", error); };

            _log.Debug("Navigating to URL: {url}", url);

            var response = await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _config.MaxWaitTimeMs
            });

            if (!response?.Ok ?? false)
            {
                _log.Warning("Failed to load page. Status: {status}", response.Status);
                return new List<string>();
            }

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

            // Wait a short moment for initial JavaScript to execute
            await page.WaitForTimeoutAsync(100);

            // Wait for the selector to appear
            _log.Debug("Waiting for selector: {selector}", selector);

            try
            {
                await page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions
                {
                    Timeout = _config.MaxWaitTimeMs
                });
            }
            catch (TimeoutException)
            {
                _log.Warning("Selector '{selector}' not found within timeout.", selector);
                return new List<string>();
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
            _semaphore.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            await _browser.CloseAsync();
            _playwright.Dispose();
            _semaphore.Dispose();
            _disposed = true;
        }
    }
}