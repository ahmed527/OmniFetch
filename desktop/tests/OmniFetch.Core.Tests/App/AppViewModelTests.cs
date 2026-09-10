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

    [Fact]
    public void DownloadItemViewModel_IsSelectedProperty_RaisesPropertyChangedAndDefaultsToFalse()
    {
        var item = new DownloadItemViewModel(Guid.NewGuid(), "https://example.com/test.mp4", "/tmp/test.mp4");
        Assert.False(item.IsSelected);

        string? changedProp = null;
        item.PropertyChanged += (s, e) => changedProp = e.PropertyName;

        item.IsSelected = true;
        Assert.True(item.IsSelected);
        Assert.Equal(nameof(DownloadItemViewModel.IsSelected), changedProp);
    }

    [Fact]
    public void MultiSelection_BulkFilter_CorrectlyIdentifiesPausableAndResumableItems()
    {
        var item1 = new DownloadItemViewModel(Guid.NewGuid(), "https://example.com/1.mp4", "/tmp/1.mp4");
        var item2 = new DownloadItemViewModel(Guid.NewGuid(), "https://example.com/2.mp4", "/tmp/2.mp4");
        var item3 = new DownloadItemViewModel(Guid.NewGuid(), "https://example.com/3.mp4", "/tmp/3.mp4");

        item1.SetStatus(DownloadStatus.Downloading); // CanPause
        item2.SetStatus(DownloadStatus.Paused);      // CanResume
        item3.SetStatus(DownloadStatus.Downloading); // CanPause

        // Select all 3
        item1.IsSelected = true;
        item2.IsSelected = true;
        item3.IsSelected = true;

        var selected = new List<DownloadItemViewModel> { item1, item2, item3 };

        var pausable = selected.Where(d => d.CanPause).ToList();
        var resumable = selected.Where(d => d.CanResume).ToList();

        Assert.Equal(2, pausable.Count);
        Assert.Contains(item1, pausable);
        Assert.Contains(item3, pausable);

        Assert.Single(resumable);
        Assert.Contains(item2, resumable);
    }

    [Fact]
    public void DragSelection_RangeCalculation_SelectsContinuousSubset()
    {
        var items = new List<DownloadItemViewModel>
        {
            new(Guid.NewGuid(), "https://example.com/0.mp4", "/tmp/0.mp4"),
            new(Guid.NewGuid(), "https://example.com/1.mp4", "/tmp/1.mp4"),
            new(Guid.NewGuid(), "https://example.com/2.mp4", "/tmp/2.mp4"),
            new(Guid.NewGuid(), "https://example.com/3.mp4", "/tmp/3.mp4"),
            new(Guid.NewGuid(), "https://example.com/4.mp4", "/tmp/4.mp4"),
        };

        // Simulate downward drag from row 1 to row 3
        int startIdx = 1;
        int endIdx = 3;
        int min = Math.Min(startIdx, endIdx);
        int max = Math.Max(startIdx, endIdx);

        for (int i = 0; i < items.Count; i++)
        {
            items[i].IsSelected = (i >= min && i <= max);
        }

        Assert.False(items[0].IsSelected);
        Assert.True(items[1].IsSelected);
        Assert.True(items[2].IsSelected);
        Assert.True(items[3].IsSelected);
        Assert.False(items[4].IsSelected);

        // Simulate upward drag from row 4 to row 2
        startIdx = 4;
        endIdx = 2;
        min = Math.Min(startIdx, endIdx);
        max = Math.Max(startIdx, endIdx);

        for (int i = 0; i < items.Count; i++)
        {
            items[i].IsSelected = (i >= min && i <= max);
        }

        Assert.False(items[0].IsSelected);
        Assert.False(items[1].IsSelected);
        Assert.True(items[2].IsSelected);
        Assert.True(items[3].IsSelected);
        Assert.True(items[4].IsSelected);
    }

    [Fact]
    public void DragSelection_MarqueeCoordinates_ClampsToValidIndexRange()
    {
        int itemCount = 5;
        double rowHeight = 38.0;

        // Box from Y=50 to Y=120 (approx row 1 to row 3)
        double boxTop = 50.0;
        double boxHeight = 70.0;

        int rawStart = (int)(boxTop / rowHeight);
        int rawEnd = (int)((boxTop + boxHeight) / rowHeight);

        int clampedStart = Math.Clamp(rawStart, 0, itemCount - 1);
        int clampedEnd = Math.Clamp(rawEnd, 0, itemCount - 1);

        Assert.Equal(1, clampedStart);
        Assert.Equal(3, clampedEnd);

        // Negative coordinate drag (dragged above table top)
        boxTop = -30.0;
        boxHeight = 40.0;

        rawStart = (int)(boxTop / rowHeight);
        rawEnd = (int)((boxTop + boxHeight) / rowHeight);

        clampedStart = Math.Clamp(rawStart, 0, itemCount - 1);
        clampedEnd = Math.Clamp(rawEnd, 0, itemCount - 1);

        Assert.Equal(0, clampedStart);
        Assert.Equal(0, clampedEnd);

        // Way beyond table bottom
        boxTop = 500.0;
        boxHeight = 100.0;

        rawStart = (int)(boxTop / rowHeight);
        rawEnd = (int)((boxTop + boxHeight) / rowHeight);

        clampedStart = Math.Clamp(rawStart, 0, itemCount - 1);
        clampedEnd = Math.Clamp(rawEnd, 0, itemCount - 1);

        Assert.Equal(4, clampedStart);
        Assert.Equal(4, clampedEnd);
    }

    [Fact]
    public void DragSelection_PixelPerfectGeometry_CalculatesExactRectAndDirection()
    {
        // Initial click anchor point (e.g., clicked at X=150, Y=80)
        double startX = 150.0;
        double startY = 80.0;

        // 1. Drag Down-Right to (220, 160)
        double currX = 220.0;
        double currY = 160.0;
        double minX = Math.Min(startX, currX);
        double minY = Math.Min(startY, currY);
        double width = Math.Max(2, Math.Max(startX, currX) - minX);
        double height = Math.Max(2, Math.Max(startY, currY) - minY);

        Assert.Equal(150.0, minX);
        Assert.Equal(80.0, minY);
        Assert.Equal(70.0, width);
        Assert.Equal(80.0, height);

        // 2. Drag Up-Left to (90, 30)
        currX = 90.0;
        currY = 30.0;
        minX = Math.Min(startX, currX);
        minY = Math.Min(startY, currY);
        width = Math.Max(2, Math.Max(startX, currX) - minX);
        height = Math.Max(2, Math.Max(startY, currY) - minY);

        Assert.Equal(90.0, minX);
        Assert.Equal(30.0, minY);
        Assert.Equal(60.0, width);
        Assert.Equal(50.0, height);
        // Bottom-Right corner of box must be the initial click point
        Assert.Equal(150.0, minX + width);
        Assert.Equal(80.0, minY + height);

        // 3. Drag Up-Right to (250, 40)
        currX = 250.0;
        currY = 40.0;
        minX = Math.Min(startX, currX);
        minY = Math.Min(startY, currY);
        width = Math.Max(2, Math.Max(startX, currX) - minX);
        height = Math.Max(2, Math.Max(startY, currY) - minY);

        Assert.Equal(150.0, minX);
        Assert.Equal(40.0, minY);
        Assert.Equal(100.0, width);
        Assert.Equal(40.0, height);

        // 4. Drag Down-Left to (80, 140)
        currX = 80.0;
        currY = 140.0;
        minX = Math.Min(startX, currX);
        minY = Math.Min(startY, currY);
        width = Math.Max(2, Math.Max(startX, currX) - minX);
        height = Math.Max(2, Math.Max(startY, currY) - minY);

        Assert.Equal(80.0, minX);
        Assert.Equal(80.0, minY);
        Assert.Equal(70.0, width);
        Assert.Equal(60.0, height);
    }

    [Fact]
    public void DragSelection_UpwardDrag_TracksCursorIndexAsSelectedDownload()
    {
        var items = new List<DownloadItemViewModel>
        {
            new(Guid.NewGuid(), "https://example.com/0.bin", "/tmp/0.bin"),
            new(Guid.NewGuid(), "https://example.com/1.bin", "/tmp/1.bin"),
            new(Guid.NewGuid(), "https://example.com/2.bin", "/tmp/2.bin"),
            new(Guid.NewGuid(), "https://example.com/3.bin", "/tmp/3.bin")
        };

        // Drag upward from index 3 up to index 1
        int startIndex = 3;
        int endIndex = 1;
        int start = Math.Clamp(Math.Min(startIndex, endIndex), 0, items.Count - 1);
        int end = Math.Clamp(Math.Max(startIndex, endIndex), 0, items.Count - 1);

        for (int i = 0; i < items.Count; i++)
        {
            items[i].IsSelected = (i >= start && i <= end);
        }
        var selectedDownload = items[Math.Clamp(endIndex, 0, items.Count - 1)];

        Assert.False(items[0].IsSelected);
        Assert.True(items[1].IsSelected);
        Assert.True(items[2].IsSelected);
        Assert.True(items[3].IsSelected);

        // The active item under cursor must be items[1] (index 1), not items[3]!
        Assert.Equal(items[1], selectedDownload);
    }
}

