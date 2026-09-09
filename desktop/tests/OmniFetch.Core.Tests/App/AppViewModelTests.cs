using System;
using System.Collections.Generic;
using Microsoft.Maui.Graphics;
using OmniFetch.App.Models;
using OmniFetch.App.ViewModels;
using OmniFetch.Core.Common;
using OmniFetch.Core.Models;
using Xunit;

namespace OmniFetch.Core.Tests.App;

public class AppViewModelTests
{
    [Fact]
    public void CategoryDeduction_CorrectlyCategorizesFileExtensions()
    {
        Assert.Equal(CategoryFilterType.Compressed, DownloadItemViewModel.DeduceCategory("ubuntu-24.04.iso.zip"));
        Assert.Equal(CategoryFilterType.Compressed, DownloadItemViewModel.DeduceCategory("archive.tar.gz"));
        Assert.Equal(CategoryFilterType.Compressed, DownloadItemViewModel.DeduceCategory("backup.7z"));

        Assert.Equal(CategoryFilterType.Video, DownloadItemViewModel.DeduceCategory("movie.mp4"));
        Assert.Equal(CategoryFilterType.Video, DownloadItemViewModel.DeduceCategory("recording.mkv"));
        Assert.Equal(CategoryFilterType.Video, DownloadItemViewModel.DeduceCategory("clip.mov"));

        Assert.Equal(CategoryFilterType.Music, DownloadItemViewModel.DeduceCategory("track01.flac"));
        Assert.Equal(CategoryFilterType.Music, DownloadItemViewModel.DeduceCategory("podcast.mp3"));

        Assert.Equal(CategoryFilterType.Documents, DownloadItemViewModel.DeduceCategory("report.pdf"));
        Assert.Equal(CategoryFilterType.Documents, DownloadItemViewModel.DeduceCategory("document.docx"));
        Assert.Equal(CategoryFilterType.Documents, DownloadItemViewModel.DeduceCategory("sheet.xlsx"));

        Assert.Equal(CategoryFilterType.Programs, DownloadItemViewModel.DeduceCategory("installer.dmg"));
        Assert.Equal(CategoryFilterType.Programs, DownloadItemViewModel.DeduceCategory("setup.pkg"));

        Assert.Equal(CategoryFilterType.All, DownloadItemViewModel.DeduceCategory("unknown.xyz123"));
    }

    [Theory]
    [InlineData(500, "500 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1024 * 1024 * 2.5, "2.50 MB")]
    [InlineData(1024L * 1024 * 1024 * 4, "4.00 GB")]
    public void FormatBytes_ProducesHumanReadableOutput(double bytes, string expected)
    {
        Assert.Equal(expected, DownloadItemViewModel.FormatBytes(bytes));
    }

    [Fact]
    public void FormatDuration_ProducesCorrectText()
    {
        Assert.Equal("45 sec", DownloadItemViewModel.FormatDuration(TimeSpan.FromSeconds(45)));
        Assert.Equal("2 min 15 sec", DownloadItemViewModel.FormatDuration(TimeSpan.FromSeconds(135)));
        Assert.Equal("1 hr 30 min", DownloadItemViewModel.FormatDuration(TimeSpan.FromMinutes(90)));
    }

    [Fact]
    public void SegmentDisplayItem_CalculatesRelativeWidthAndFillFractionAccurately()
    {
        const long totalFileSize = 100_000_000; // 100 MB
        var seg = new SegmentDisplayItem
        {
            SegmentIndex = 1,
            StartByte = 0,
            EndByte = 49_999_999, // 50 MB
            CurrentByte = 25_000_000, // 25 MB downloaded (50%)
            Status = SegmentStatus.Downloading
        };

        seg.UpdateMetrics(totalFileSize, isCompleted: false, speedBytesPerSec: 15_000_000);

        Assert.Equal(50_000_000, seg.SegmentSize);
        Assert.Equal(25_000_000, seg.DownloadedBytes);
        Assert.Equal(0.5, seg.FillFraction, 3);
        Assert.Equal(0.5, seg.RelativeWidth, 3);
        Assert.Equal(SegmentStatus.Downloading, seg.Status);
        Assert.Contains("14.31 MB/s", seg.SpeedText);
    }

    [Fact]
    public void SegmentDisplayItem_MarksCompletedCorrectly()
    {
        const long totalFileSize = 100_000_000;
        var seg = new SegmentDisplayItem
        {
            SegmentIndex = 2,
            StartByte = 50_000_000,
            EndByte = 99_999_999,
            CurrentByte = 100_000_000 // Fully completed
        };

        seg.UpdateMetrics(totalFileSize, isCompleted: true, speedBytesPerSec: 0);

        Assert.Equal(1.0, seg.FillFraction, 3);
        Assert.Equal(SegmentStatus.Completed, seg.Status);
        Assert.Equal("Complete", seg.StatusText);
        Assert.Equal("-", seg.SpeedText);
    }

    [Fact]
    public void DownloadItemViewModel_UpdatesFromTelemetrySnapshot()
    {
        var jobId = Guid.NewGuid();
        var item = new DownloadItemViewModel(jobId, "https://example.com/test.iso", "/tmp/test.iso", 100_000_000);

        Assert.Equal(DownloadStatus.Queued, item.Status);
        Assert.Equal("test.iso", item.FileName);
        Assert.Equal(CategoryFilterType.Compressed, item.Category);

        var snapshot = new DownloadProgressSnapshot
        {
            JobId = jobId,
            Status = DownloadStatus.Downloading,
            TotalBytes = 100_000_000,
            DownloadedBytes = 40_000_000,
            ProgressPercentage = 40.0,
            SmoothedSpeedBytesPerSecond = 20_000_000,
            EstimatedTimeRemaining = TimeSpan.FromSeconds(3),
            ActiveConnections = 2,
            Segments = new List<SegmentProgressSnapshot>
            {
                new()
                {
                    SegmentIndex = 1,
                    StartByte = 0,
                    EndByte = 49_999_999,
                    CurrentByte = 30_000_000,
                    IsCompleted = false,
                    SpeedBytesPerSecond = 12_000_000
                },
                new()
                {
                    SegmentIndex = 2,
                    StartByte = 50_000_000,
                    EndByte = 99_999_999,
                    CurrentByte = 60_000_000,
                    IsCompleted = false,
                    SpeedBytesPerSecond = 8_000_000
                }
            }
        };

        item.SetStatus(DownloadStatus.Downloading);
        item.UpdateFromSnapshot(snapshot);

        Assert.Equal(40_000_000, item.DownloadedBytes);
        Assert.Equal(40.0, item.ProgressPercentage);
        Assert.Equal(0.4, item.ProgressFraction, 3);
        Assert.Equal("3 sec", item.TimeLeftText);
        Assert.Equal(2, item.Segments.Count);

        // Check segment 1
        Assert.Equal(1, item.Segments[0].SegmentIndex);
        Assert.Equal(0.6, item.Segments[0].FillFraction, 3);
        Assert.Equal(0.5, item.Segments[0].RelativeWidth, 3);

        // Check segment 2
        Assert.Equal(2, item.Segments[1].SegmentIndex);
        Assert.Equal(0.2, item.Segments[1].FillFraction, 3);
        Assert.Equal(0.5, item.Segments[1].RelativeWidth, 3);
    }

    [Fact]
    public void DownloadItemViewModel_StatusTransitionsUpdateFlags()
    {
        var item = new DownloadItemViewModel(Guid.NewGuid(), "https://example.com/file.zip", "/tmp/file.zip", 1000);

        Assert.True(item.CanResume);
        Assert.False(item.CanPause);
        Assert.False(item.IsDownloading);

        item.SetStatus(DownloadStatus.Downloading);
        Assert.False(item.CanResume);
        Assert.True(item.CanPause);
        Assert.True(item.IsDownloading);

        item.SetStatus(DownloadStatus.Completed);
        Assert.False(item.CanResume);
        Assert.False(item.CanPause);
        Assert.False(item.IsDownloading);
        Assert.Equal(100.0, item.ProgressPercentage);
        Assert.Equal("100% Complete", item.StatusText);
        Assert.Equal("Completed", item.TransferRateText);
    }

    [Fact]
    public void DownloadItemViewModel_DestinationFolder_And_SavePathDisplay_Are_Correct()
    {
        string filePath = "/Users/testuser/Downloads/Video/awesome_clip.mp4";
        var item = new DownloadItemViewModel(Guid.NewGuid(), "https://example.com/clip.mp4", filePath, 5000);

        Assert.Equal("/Users/testuser/Downloads/Video", item.DestinationFolder);
        Assert.Equal(filePath, item.SavePathDisplay);

        item.DestinationFilePath = "/Users/testuser/Downloads/awesome_clip.mp4";
        Assert.Equal("/Users/testuser/Downloads", item.DestinationFolder);
        Assert.Equal("/Users/testuser/Downloads/awesome_clip.mp4", item.SavePathDisplay);
    }

    [Fact]
    public void OptionsViewModel_MaxConnections_StepCommands_WorkWithinBounds()
    {
        var settingsService = new OmniFetch.Core.Settings.SettingsService(string.Empty);
        var vm = new OptionsViewModel(settingsService);

        vm.MaxConnections = 8;
        vm.IncreaseMaxConnections();
        Assert.Equal(9, vm.MaxConnections);

        vm.DecreaseMaxConnections();
        Assert.Equal(8, vm.MaxConnections);

        vm.MaxConnections = 32;
        vm.IncreaseMaxConnections();
        Assert.Equal(32, vm.MaxConnections); // clamped at 32

        vm.MaxConnections = 1;
        vm.DecreaseMaxConnections();
        Assert.Equal(1, vm.MaxConnections); // clamped at 1
    }

    [Fact]
    public void OptionsViewModel_ChangingDefaultDownloadDirectory_ImmediatelyUpdatesAllCategoryDirectories()
    {
        var settingsService = new OmniFetch.Core.Settings.SettingsService(string.Empty);
        var vm = new OptionsViewModel(settingsService);

        string newDefault = "/Users/testuser/MyDownloads";
        vm.DefaultDownloadDirectory = newDefault;

        Assert.Equal(newDefault, vm.DefaultDownloadDirectory);
        Assert.Equal(Path.Combine(newDefault, "Video"), vm.VideoDirectory);
        Assert.Equal(Path.Combine(newDefault, "Music"), vm.MusicDirectory);
        Assert.Equal(Path.Combine(newDefault, "Compressed"), vm.CompressedDirectory);
        Assert.Equal(Path.Combine(newDefault, "Documents"), vm.DocumentsDirectory);
        Assert.Equal(Path.Combine(newDefault, "Programs"), vm.ProgramsDirectory);
    }

    [Fact]
    public void OmniFetchSettings_UpdateDefaultDownloadDirectory_UpdatesAllCategories()
    {
        var settings = new OmniFetch.Core.Settings.OmniFetchSettings();
        string newRoot = "/Users/testuser/Desktop";

        settings.UpdateDefaultDownloadDirectory(newRoot);

        Assert.Equal(newRoot, settings.DefaultDownloadDirectory);
        Assert.Equal(newRoot, settings.CategoryDirectories["General"]);
        Assert.Equal(Path.Combine(newRoot, "Video"), settings.CategoryDirectories["Video"]);
        Assert.Equal(Path.Combine(newRoot, "Music"), settings.CategoryDirectories["Music"]);
        Assert.Equal(Path.Combine(newRoot, "Compressed"), settings.CategoryDirectories["Compressed"]);
        Assert.Equal(Path.Combine(newRoot, "Documents"), settings.CategoryDirectories["Documents"]);
        Assert.Equal(Path.Combine(newRoot, "Programs"), settings.CategoryDirectories["Programs"]);
    }

    [Fact]
    public void DownloadItemViewModel_CanStopAndCanResume_ReflectsStatusCorrectly()
    {
        var item = new DownloadItemViewModel(Guid.NewGuid(), "https://example.com/test.mp4", "/path/test.mp4", 1024);

        // Downloading
        item.SetStatus(DownloadStatus.Downloading);
        Assert.True(item.CanStop);
        Assert.False(item.CanResume);
        Assert.False(item.IsCompleted);

        // Completed
        item.SetStatus(DownloadStatus.Completed);
        Assert.False(item.CanStop);
        Assert.False(item.CanResume);
        Assert.True(item.IsCompleted);

        // Paused
        item.SetStatus(DownloadStatus.Paused);
        Assert.False(item.CanStop);
        Assert.True(item.CanResume);
        Assert.False(item.IsCompleted);

        // Queued
        item.SetStatus(DownloadStatus.Queued);
        Assert.True(item.CanStop);
        Assert.True(item.CanResume);
        Assert.False(item.IsCompleted);

        // Failed
        item.SetStatus(DownloadStatus.Failed);
        Assert.False(item.CanStop);
        Assert.True(item.CanResume);
        Assert.False(item.IsCompleted);
    }
}

