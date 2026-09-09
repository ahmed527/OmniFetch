using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using OmniFetch.App.Models;

namespace OmniFetch.App.Views.Controls;

public partial class SegmentedProgressBar : ContentView
{
    public static readonly BindableProperty SegmentsProperty =
        BindableProperty.Create(
            nameof(Segments),
            typeof(IList<SegmentDisplayItem>),
            typeof(SegmentedProgressBar),
            null,
            propertyChanged: OnSegmentsChanged);

    public static readonly BindableProperty ProgressPercentageProperty =
        BindableProperty.Create(
            nameof(ProgressPercentage),
            typeof(double),
            typeof(SegmentedProgressBar),
            0.0,
            propertyChanged: OnProgressPercentageChanged);

    private readonly SegmentedBarDrawable _drawable = new();

    public IList<SegmentDisplayItem>? Segments
    {
        get => (IList<SegmentDisplayItem>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public double ProgressPercentage
    {
        get => (double)GetValue(ProgressPercentageProperty);
        set => SetValue(ProgressPercentageProperty, value);
    }

    public SegmentedProgressBar()
    {
        InitializeComponent();
        CanvasView.Drawable = _drawable;
    }

    private static void OnSegmentsChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is SegmentedProgressBar control)
        {
            if (oldValue is INotifyCollectionChanged oldObs)
            {
                oldObs.CollectionChanged -= control.OnCollectionChanged;
            }

            if (newValue is IList<SegmentDisplayItem> newSegs)
            {
                control._drawable.Segments = newSegs;
                if (newValue is INotifyCollectionChanged newObs)
                {
                    newObs.CollectionChanged += control.OnCollectionChanged;
                }
            }

            control.CanvasView.Invalidate();
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is INotifyPropertyChanged npc)
                {
                    npc.PropertyChanged += OnItemPropertyChanged;
                }
            }
        }

        CanvasView.Invalidate();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        CanvasView.Invalidate();
    }

    private static void OnProgressPercentageChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is SegmentedProgressBar control && newValue is double pct)
        {
            control._drawable.ProgressPercentage = pct;
            control.CanvasView.Invalidate();
        }
    }

    private sealed class SegmentedBarDrawable : IDrawable
    {
        public IList<SegmentDisplayItem>? Segments { get; set; }
        public double ProgressPercentage { get; set; }

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            float width = dirtyRect.Width;
            float height = dirtyRect.Height;

            // 1. Draw recessed background track
            canvas.FillColor = Color.FromArgb("#1E293B");
            canvas.FillRectangle(0, 0, width, height);

            // Subtle IDM grid markers every 10%
            canvas.StrokeColor = Color.FromArgb("#334155");
            canvas.StrokeSize = 1;
            for (int i = 1; i < 10; i++)
            {
                float markX = (width * i) / 10f;
                canvas.DrawLine(markX, 0, markX, height);
            }

            if (Segments == null || Segments.Count == 0)
            {
                // Fallback single bar when no segment details
                if (ProgressPercentage > 0)
                {
                    float filledWidth = (float)(width * Math.Clamp(ProgressPercentage / 100.0, 0.0, 1.0));
                    canvas.FillColor = Color.FromArgb("#10B981");
                    canvas.FillRectangle(0, 0, filledWidth, height);
                }
                return;
            }

            // 2. Draw each segment proportionally across the bar
            float currentX = 0;
            double totalWidthRatio = 0;
            foreach (var s in Segments)
            {
                totalWidthRatio += s.RelativeWidth;
            }
            if (totalWidthRatio <= 0) totalWidthRatio = 1.0;

            for (int i = 0; i < Segments.Count; i++)
            {
                var seg = Segments[i];
                float segWidth = (float)(width * (seg.RelativeWidth / totalWidthRatio));

                // Segment boundary track
                canvas.FillColor = Color.FromArgb("#0F172A");
                canvas.FillRectangle(currentX, 0, segWidth, height);

                // Segment filled portion
                float filledSegWidth = (float)(segWidth * Math.Clamp(seg.FillFraction, 0.0, 1.0));
                if (filledSegWidth > 0)
                {
                    canvas.FillColor = seg.SegmentColor;
                    canvas.FillRectangle(currentX, 0, filledSegWidth, height);

                    // IDM glossy top highlight line
                    canvas.FillColor = Colors.White.WithAlpha(0.25f);
                    canvas.FillRectangle(currentX, 0, filledSegWidth, height * 0.4f);
                }

                // IDM active read needle / worker pointer marker
                if (seg.Status == Models.SegmentStatus.Downloading && filledSegWidth > 0 && filledSegWidth < segWidth)
                {
                    float needleX = currentX + filledSegWidth;
                    canvas.FillColor = Color.FromArgb("#FACC15"); // Yellow caret
                    canvas.FillRectangle(needleX - 1, 0, 2, height);
                }

                // Segment divider line
                if (i < Segments.Count - 1)
                {
                    canvas.StrokeColor = Color.FromArgb("#020617");
                    canvas.StrokeSize = 1.5f;
                    canvas.DrawLine(currentX + segWidth, 0, currentX + segWidth, height);
                }

                currentX += segWidth;
            }
        }
    }
}
