using Octokit;
using Polly;
using Polly.Retry;
using Zarichney.Config;
using FileMode = Octokit.FileMode;
using ILogger = Serilog.ILogger;

namespace Zarichney.Services;

public class GitHubConfig : IConfig
{
  public string RepositoryOwner { get; init; } = string.Empty;
  public string RepositoryName { get; init; } = string.Empty;
  public string BranchName { get; init; } = "main";
  public string AccessToken { get; init; } = string.Empty;
  public int RetryAttempts { get; init; } = 5;
}

public interface IGitHubService
{
  Task CommitFileAsync(string filePath, byte[] content, string directory, string commitMessage);
}

public class GitHubService(
  GitHubConfig config
) : IGitHubService
{
  private static readonly ILogger Log = Serilog.Log.ForContext<GitHubService>();

  private readonly GitHubClient _client = new(new ProductHeaderValue(config.RepositoryName))
  {
    Credentials = new Credentials(config.AccessToken)
  };

  private readonly AsyncRetryPolicy _retryPolicy = Policy
    .Handle<RateLimitExceededException>()
    .WaitAndRetryAsync(
      retryCount: config.RetryAttempts,
      sleepDurationProvider: _ => TimeSpan.FromSeconds(1),
      onRetry: (exception, _, retryCount, context) =>
      {
        Log.Warning(exception,
          "GitHub operation attempt {retryCount}: Retrying due to {exception}. Retry Context: {@Context}",
          retryCount, exception.Message, context);
      }
    );

  public async Task CommitFileAsync(string filePath, byte[] content, string directory, string commitMessage)
  {
    try
    {
      Log.Information("Starting GitHub commit for file {FilePath} in directory {Directory}",
        filePath, directory);

      await _retryPolicy.ExecuteAsync(async () =>
      {
        try
        {
          // Get the current reference for the branch because we need the latest commit SHA
          var reference = await _client.Git.Reference.Get(
            config.RepositoryOwner,
            config.RepositoryName,
            $"heads/{config.BranchName}"
          );

          var latestCommit = await _client.Git.Commit.Get(
            config.RepositoryOwner,
            config.RepositoryName,
            reference.Object.Sha
          );

          var blob = await _client.Git.Blob.Create(
            config.RepositoryOwner,
            config.RepositoryName,
            new NewBlob
            {
              Content = Convert.ToBase64String(content),
              Encoding = EncodingType.Base64
            }
          );

          // Create tree with the new file
          var newTree = new NewTree
          {
            BaseTree = latestCommit.Tree.Sha
          };

          newTree.Tree.Add(new NewTreeItem
          {
            Path = Path.Combine(directory, filePath).Replace("\\", "/"),
            Mode = FileMode.File,
            Type = TreeType.Blob,
            Sha = blob.Sha,
          });

          var tree = await _client.Git.Tree.Create(config.RepositoryOwner, config.RepositoryName, newTree);

          var commit = await _client.Git.Commit.Create(
            config.RepositoryOwner,
            config.RepositoryName,
            new NewCommit(commitMessage, tree.Sha, reference.Object.Sha)
          );

          // Update reference to point to the new commit
          await _client.Git.Reference.Update(
            config.RepositoryOwner,
            config.RepositoryName,
            $"heads/{config.BranchName}",
            new ReferenceUpdate(commit.Sha)
          );

          Log.Information("Successfully committed file {FilePath} to {Directory}. Commit SHA: {CommitSha}",
            filePath, directory, commit.Sha);
        }
        catch (Exception e)
        {
          Log.Error(e, "Error occurred during GitHub commit operation");
          throw;
        }
      });
    }
    catch (Exception e)
    {
      Log.Error(e, "Failed to commit file {FilePath} to GitHub after all retry attempts", filePath);
      throw;
    }
  }
}