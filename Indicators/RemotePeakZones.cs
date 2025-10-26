using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Windows.Media;
using ATAS.Indicators;
using OFT.Attributes;
using OFT.Rendering.Context;
using OFT.Rendering.Settings;
using RemoteIndicator.ATAS.Base;
using RemoteIndicator.ATAS.Utilities;
using IndicatorService;
using Utils.Common.Logging;
using Element = IndicatorService.IndicatorElement;
using Color = System.Drawing.Color;

namespace RemoteIndicator.ATAS.Indicators
{
    /// <summary>
    /// Remote Peak Zones Indicator
    /// Displays PEAK_HIGH and PEAK_LOW zones detected by the remote service
    /// </summary>
    [Category("Community")]
    [DisplayName("Remote Peak Zones v1.0")]
    public class RemotePeakZones : RemoteIndicatorBase
    {
        #region Configuration

        // Base colors (opaque, user-customizable) - matching Python visualization
        private Color _peakHighColorBase = Color.FromArgb(255, 255, 0, 0);   // Red (resistance)
        private Color _peakLowColorBase = Color.FromArgb(255, 0, 255, 0);    // Green (support)

        // Current colors (with alpha applied)
        private Color _peakHighColor = Color.FromArgb(77, 255, 0, 0);        // 30% opacity
        private Color _peakLowColor = Color.FromArgb(77, 0, 255, 0);

        private int _zoneOpacity = 30; // Match Python's 0.3 alpha
        private int _borderWidth = 1;
        private int _centerLineWidth = 2;

        // Display filter options
        private bool _showOnlyValidated = true;
        private bool _showPeakMarkers = true;
        private bool _showZoneRectangles = true;
        private bool _showDetectionMarker = true;

        [Display(Name = "Show Only Validated", GroupName = "Filter", Order = 10)]
        public bool ShowOnlyValidated
        {
            get => _showOnlyValidated;
            set
            {
                _showOnlyValidated = value;
                RedrawChart();
            }
        }

        [Display(Name = "Show Peak Markers", GroupName = "Display", Order = 15)]
        public bool ShowPeakMarkers
        {
            get => _showPeakMarkers;
            set
            {
                _showPeakMarkers = value;
                RedrawChart();
            }
        }

        [Display(Name = "Show Zone Rectangles", GroupName = "Display", Order = 18)]
        public bool ShowZoneRectangles
        {
            get => _showZoneRectangles;
            set
            {
                _showZoneRectangles = value;
                RedrawChart();
            }
        }

        [Display(Name = "Show Detection Marker", GroupName = "Display", Order = 19)]
        public bool ShowDetectionMarker
        {
            get => _showDetectionMarker;
            set
            {
                _showDetectionMarker = value;
                RedrawChart();
            }
        }

        [Display(Name = "Zone Opacity (0-100)", GroupName = "Display", Order = 20)]
        public int ZoneOpacity
        {
            get => _zoneOpacity;
            set
            {
                _zoneOpacity = Math.Max(0, Math.Min(100, value));
                UpdateColors();
                RedrawChart();
            }
        }

        [Display(Name = "Border Width", GroupName = "Display", Order = 25)]
        public int BorderWidth
        {
            get => _borderWidth;
            set
            {
                _borderWidth = Math.Max(1, Math.Min(5, value));
                RedrawChart();
            }
        }

        [Display(Name = "Center Line Width", GroupName = "Display", Order = 26)]
        public int CenterLineWidth
        {
            get => _centerLineWidth;
            set
            {
                _centerLineWidth = Math.Max(1, Math.Min(5, value));
                RedrawChart();
            }
        }

        [Display(Name = "Peak High Color", GroupName = "Colors", Order = 30)]
        public Color PeakHighColor
        {
            get => _peakHighColorBase;
            set
            {
                // Store user-selected base color (opaque)
                _peakHighColorBase = Color.FromArgb(255, value.R, value.G, value.B);
                // Rebuild color with current opacity
                UpdateColors();
                RedrawChart();
            }
        }

        [Display(Name = "Peak Low Color", GroupName = "Colors", Order = 40)]
        public Color PeakLowColor
        {
            get => _peakLowColorBase;
            set
            {
                // Store user-selected base color (opaque)
                _peakLowColorBase = Color.FromArgb(255, value.R, value.G, value.B);
                // Rebuild color with current opacity
                UpdateColors();
                RedrawChart();
            }
        }


        #endregion

        #region Extension Point Implementation

        protected override string IndicatorType => "extreme_price";

        private int _lastRenderedCount = 0;

        protected override void RenderElements(RenderContext context, List<Element> elements)
        {
            // Filter zones based on validation status if configured
            int filteredCount = 0;
            foreach (var element in elements)
            {
                bool isValidated = element.GetValidated();

                // Skip unvalidated zones if ShowOnlyValidated is enabled
                if (_showOnlyValidated && !isValidated)
                {
                    continue;
                }

                RenderZone(context, element, isValidated);
                filteredCount++;
            }

            // Only log when element count changes (avoid log spam)
            if (filteredCount != _lastRenderedCount)
            {
                var lastCandle = GetCandle(CurrentBar - 1);
                string timestamp = lastCandle != null ? lastCandle.LastTime.ToString("yyyy-MM-dd HH:mm:ss") : "N/A";
                this.LogInfo($"[RenderElements] Rendering {filteredCount}/{elements.Count} zones (ShowOnlyValidated={_showOnlyValidated}) | CurrentBar={CurrentBar}, Time={timestamp}");
                _lastRenderedCount = filteredCount;
            }
        }

        #endregion

        #region Rendering Logic

        private void RenderZone(RenderContext context, Element element, bool isValidated)
        {
            try
            {
                // Extract properties from element
                string type = element.GetElementType();
                double upperPrice = element.GetUpperPrice();
                double lowerPrice = element.GetLowerPrice();

                // Find bar index for detection point
                int detectionBar = FindBarByTickTime(element.ElementTickTimeMs);

                if (detectionBar < 0)
                {
                    return; // Bar outside loaded data range
                }

                // Validate prices
                if (upperPrice <= 0 || lowerPrice <= 0 || upperPrice < lowerPrice)
                {
                    return; // Invalid price data
                }

                // Determine colors based on zone type (matching Python visualization)
                bool isHigh = type.Contains("HIGH");
                Color fillColor = isHigh ? _peakHighColor : _peakLowColor;
                Color borderColor = isHigh ? _peakHighColorBase : _peakLowColorBase; // Opaque for border

                // Unvalidated zones: use gray color with transparent border for visual distinction
                if (!isValidated)
                {
                    int currentAlpha = fillColor.A;
                    fillColor = Color.FromArgb(currentAlpha, 150, 150, 150);  // Gray fill
                    borderColor = Color.FromArgb(0, 120, 120, 120);           // Transparent border (invisible)
                }

                // Get visible bar range
                int firstVisibleBar = ChartInfo.PriceChartContainer.FirstVisibleBarNumber;
                int lastVisibleBar = ChartInfo.PriceChartContainer.LastVisibleBarNumber;

                // Skip zones that end before visible area
                if (CurrentBar - 1 < firstVisibleBar)
                {
                    return; // Zone completely before visible area
                }

                // Skip zones that start after visible area
                if (detectionBar > lastVisibleBar)
                {
                    return; // Zone completely after visible area
                }

                // Clip zone to visible area
                int startBar = Math.Max(detectionBar, firstVisibleBar);
                int endBar = Math.Min(CurrentBar - 1, lastVisibleBar);

                // Get X coordinates using ChartInfo
                int x1 = ChartInfo.GetXByBar(startBar);

                // to cover the last bar
                int x2 = ChartInfo.GetXByBar(endBar) + (int)ChartInfo.PriceChartContainer.BarsWidth;

                // Get Y coordinates (ATAS coordinate system: top-left is origin)
                int upperY = ChartInfo.GetYByPrice((decimal)upperPrice);
                int lowerY = ChartInfo.GetYByPrice((decimal)lowerPrice);

                int width = x2 - x1;
                int height = lowerY - upperY; // lowerY > upperY in screen coordinates

                // Validate coordinates - should be positive after clipping to visible area
                if (width <= 0 || height <= 0)
                {
                    return; // Invalid dimensions after clipping
                }

                // Negative Y coordinates mean price is outside visible range - skip this zone
                if (upperY < 0 || lowerY < 0)
                {
                    return; // Price outside visible range
                }

                // ===== Conditional Rendering Based on Settings =====

                // 1. Draw zone rectangle (if enabled)
                if (_showZoneRectangles)
                {
                    var rectangle = new System.Drawing.Rectangle(x1, upperY, width, height);

                    // Fill zone with semi-transparent color
                    context.FillRectangle(fillColor, rectangle);

                    // Draw border with opaque zone color
                    var borderPen = new OFT.Rendering.Tools.RenderPen(borderColor, _borderWidth);
                    context.DrawRectangle(borderPen, rectangle);

                    // Draw center line (dotted, using zone color - matching Python)
                    decimal centerPrice = (decimal)((upperPrice + lowerPrice) / 2.0);
                    int centerY = ChartInfo.GetYByPrice(centerPrice);

                    // Draw dotted line manually (ATAS may not support dash styles, so draw segments)
                    DrawDottedLine(context, borderColor, x1, centerY, x2, centerY, _centerLineWidth);
                }

                // 2. Draw detection point marker (vertical line at detection bar - if enabled)
                if (_showDetectionMarker && detectionBar >= firstVisibleBar && detectionBar <= lastVisibleBar)
                {
                    // Draw purple vertical line at detection point (only if in visible range)
                    int detectionX = ChartInfo.GetXByBar(detectionBar);
                    var markerPen = new OFT.Rendering.Tools.RenderPen(Color.MediumPurple, 2);
                    context.DrawLine(markerPen, detectionX, upperY, detectionX, lowerY);
                }

                // 3. Draw peak triangle marker at peak point (if enabled)
                if (_showPeakMarkers)
                {
                    DrawPeakTriangle(context, element, isHigh);
                }
            }
            catch
            {
                // Silently skip failed zone rendering
            }
        }

        #endregion

        #region Helpers

        private void UpdateColors()
        {
            // Use base colors to avoid alpha accumulation (C6 bug fix)
            int alpha = (int)(255 * (_zoneOpacity / 100.0));

            _peakHighColor = Color.FromArgb(
                alpha,
                _peakHighColorBase.R,
                _peakHighColorBase.G,
                _peakHighColorBase.B
            );

            _peakLowColor = Color.FromArgb(
                alpha,
                _peakLowColorBase.R,
                _peakLowColorBase.G,
                _peakLowColorBase.B
            );
        }

        /// <summary>
        /// Draw a dotted line by drawing small segments with gaps
        /// Simulates dash='dot' from Python visualization
        /// </summary>
        private void DrawDottedLine(RenderContext context, Color color, int x1, int y, int x2, int y2, int width)
        {
            var pen = new OFT.Rendering.Tools.RenderPen(color, width);

            // Calculate horizontal dotted line
            int dotLength = 3;
            int gapLength = 3;
            int totalLength = x2 - x1;

            for (int x = x1; x < x2; x += dotLength + gapLength)
            {
                int segmentEnd = Math.Min(x + dotLength, x2);
                context.DrawLine(pen, x, y, segmentEnd, y2);
            }
        }

        /// <summary>
        /// Draw peak triangle marker at the peak point
        /// PEAK_HIGH: Red downward triangle (▼) - resistance above
        /// PEAK_LOW: Green upward triangle (▲) - support below
        /// </summary>
        private void DrawPeakTriangle(RenderContext context, Element element, bool isHigh)
        {
            try
            {
                // Find peak point coordinates
                int peakBar = FindBarByTickTime(element.ElementTickTimeMs);
                if (peakBar < 0)
                {
                    return; // Cannot find bar for peak
                }

                int peakX = ChartInfo.GetXByBar(peakBar);
                int peakY = ChartInfo.GetYByPrice((decimal)element.ElementPrice);

                // Validate coordinates
                if (peakY < 0)
                {
                    return; // Price outside visible range
                }

                // Define triangle size
                const int halfBase = 6;  // Half of triangle base width
                const int height = 8;    // Triangle height

                // Build triangle points
                Point[] triangle;
                if (isHigh)
                {
                    // Downward triangle ▼ (peak at bottom, base at top)
                    triangle = new Point[]
                    {
                        new Point(peakX, peakY + height),        // Bottom peak
                        new Point(peakX - halfBase, peakY - 4),  // Top left
                        new Point(peakX + halfBase, peakY - 4)   // Top right
                    };
                }
                else
                {
                    // Upward triangle ▲ (peak at top, base at bottom)
                    triangle = new Point[]
                    {
                        new Point(peakX, peakY - height),        // Top peak
                        new Point(peakX - halfBase, peakY + 4),  // Bottom left
                        new Point(peakX + halfBase, peakY + 4)   // Bottom right
                    };
                }

                // Draw filled triangle
                Color markerColor = isHigh ? Color.Red : Color.Green;
                context.FillPolygon(markerColor, triangle);
            }
            catch
            {
                // Silently skip if triangle drawing fails
            }
        }

        #endregion
    }

    /// <summary>
    /// Extension methods for color conversion
    /// </summary>
    internal static class ColorExtensions
    {
        public static System.Windows.Media.Color ToMediaColor(this System.Drawing.Color color)
        {
            return System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B);
        }
    }

    /// <summary>
    /// Extension methods for IndicatorElement to extract properties
    /// </summary>
    internal static class ElementExtensions
    {
        public static string GetElementType(this Element element)
        {
            return element.Properties.TryGetValue("zone_type", out var value) ? value : "";
        }

        public static double GetUpperPrice(this Element element)
        {
            return element.Properties.TryGetValue("upper_price", out var value) && double.TryParse(value, out var price) ? price : 0.0;
        }

        public static double GetLowerPrice(this Element element)
        {
            return element.Properties.TryGetValue("lower_price", out var value) && double.TryParse(value, out var price) ? price : 0.0;
        }

        public static double GetStrength(this Element element)
        {
            return element.Properties.TryGetValue("strength", out var value) && double.TryParse(value, out var strength) ? strength : 0.0;
        }

        public static bool GetValidated(this Element element)
        {
            // Parse validated property from service (default: true for backward compatibility)
            return element.Properties.TryGetValue("validated", out var value) && bool.TryParse(value, out var validated) ? validated : true;
        }
    }
}
