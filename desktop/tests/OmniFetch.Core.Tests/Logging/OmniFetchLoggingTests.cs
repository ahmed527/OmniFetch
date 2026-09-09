using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OmniFetch.Bridge;
using OmniFetch.Core.Logging;
using Xunit;

namespace OmniFetch.Core.Tests.Logging;

public sealed class OmniFetchLoggingTests : IDisposable
{
    private readonly string _testLogDir;

    public OmniFetchLoggingTests()
    {
        _testLogDir = Path.Combine(Path.GetTempPath(), "OmniFetchLogTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testLogDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testLogDir))
            {
                Directory.Delete(_testLogDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    [Fact]
    public void IsEnabled_ReturnsTrueForErrorAndCritical_Always()
    {
        var prodConfig = new OmniFetchLogConfig
        {
            MinimumLevel = LogLevel.Error,
            IsDevelopmentMode = false,
            LogDirectory = _testLogDir
        };

        var logger = new OmniFetchFileLogger("TestCategory", prodConfig);

        Assert.False(logger.IsEnabled(LogLevel.Trace));
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.False(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Error));
        Assert.True(logger.IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void IsEnabled_EnablesAllLevelsInDevelopmentMode()
    {
        var devConfig = new OmniFetchLogConfig
        {
            MinimumLevel = LogLevel.Debug,
            IsDevelopmentMode = true,
            LogDirectory = _testLogDir
        };

        var logger = new OmniFetchFileLogger("TestCategory", devConfig);

        Assert.True(logger.IsEnabled(LogLevel.Debug));
        Assert.True(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Error));
        Assert.True(logger.IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void Log_WritesFormattedEntryAndFlushesImmediatelyOnError()
    {
        var config = new OmniFetchLogConfig
        {
            MinimumLevel = LogLevel.Error,
            LogDirectory = _testLogDir,
            LogFileName = "error_test.log"
        };

        var logger = new OmniFetchFileLogger("EngineCategory", config);

        // Debug should be skipped
        logger.LogDebug("This should not be written in production");

        // Error should be written immediately
        var testEx = new InvalidOperationException("Simulated failure");
        logger.LogError(testEx, "Fatal transaction failure occurred");

        Assert.True(File.Exists(config.FullLogPath), "Log file should exist immediately after Error log");
        string content = File.ReadAllText(config.FullLogPath);

        Assert.DoesNotContain("This should not be written", content);
        Assert.Contains("[ERROR]", content);
        Assert.Contains("[EngineCategory]", content);
        Assert.Contains("Fatal transaction failure occurred", content);
        Assert.Contains("Simulated failure", content);
        Assert.Contains("System.InvalidOperationException", content);
    }

    [Fact]
    public async Task ConcurrentLogging_HandlesHighConcurrencyWithoutCorrupting()
    {
        var config = new OmniFetchLogConfig
        {
            MinimumLevel = LogLevel.Information,
            LogDirectory = _testLogDir,
            LogFileName = "concurrent.log"
        };

        var logger = new OmniFetchFileLogger("ConcurrencyTest", config);
        const int taskCount = 10;
        const int logsPerTask = 50;

        var tasks = Enumerable.Range(0, taskCount).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < logsPerTask; i++)
            {
                logger.LogInformation("Thread {ThreadId} iteration {Iter}", t, i);
            }
        }));

        await Task.WhenAll(tasks);

        Assert.True(File.Exists(config.FullLogPath));
        string[] lines = File.ReadAllLines(config.FullLogPath);
        Assert.Equal(taskCount * logsPerTask, lines.Length);
    }

    [Fact]
    public void BridgeLogger_WritesErrorLogToConfiguredPath()
    {
        string testMessage = "Test Bridge Error " + Guid.NewGuid().ToString("N");
        BridgeLogger.LogError(testMessage);

        Assert.True(File.Exists(BridgeLogger.LogFilePath), "Bridge log file must be created on error");
        string content = File.ReadAllText(BridgeLogger.LogFilePath);
        Assert.Contains(testMessage, content);
    }
}
