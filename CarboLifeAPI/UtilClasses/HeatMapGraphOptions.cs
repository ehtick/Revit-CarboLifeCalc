/*
 * Carbo Calc Copywrite 2023
 * Render settings for the Heatmapper distribution chart.
 */
using System;

namespace CarboLifeAPI
{
    /// <summary>
    /// The settings that control how <see cref="HeatMapBarBuilder"/> draws the distribution chart.
    /// A default instance is used when none is supplied.
    /// </summary>
    public class HeatMapGraphOptions
    {
        /// <summary>
        /// Plot the value axis logarithmically, which keeps a heavily skewed carbon distribution readable.
        /// Silently falls back to a linear axis when the plotted data is not strictly positive,
        /// in which case <see cref="LogScaleUnavailable"/> is set.
        /// </summary>
        public bool UseLogScale { get; set; }

        /// <summary>
        /// True when the last render was asked for a log axis but had to use a linear one
        /// because the data contained values of zero or less.
        /// </summary>
        public bool LogScaleUnavailable { get; internal set; }

        /// <summary>
        /// The narrowest bar that is still readable, in device independent pixels.
        /// Elements are aggregated into bins so a bar is never drawn thinner than this.
        /// </summary>
        public double MinBarWidth { get; set; }

        /// <summary>
        /// The number of gridlines aimed for on the value axis. The real count follows from the
        /// nice-number step that is closest to this.
        /// </summary>
        public int TargetTickCount { get; set; }

        /// <summary>
        /// Font size for the axis labels.
        /// </summary>
        public double FontSize { get; set; }

        /// <summary>
        /// The value axis is anchored at zero when the lowest plotted value sits below this fraction
        /// of the highest one. Above it the axis is truncated to the data - and marked as truncated -
        /// so that a tight band of values (say 1.00 to 1.05 kgCO2/kg) stays readable instead of
        /// collapsing into a row of equal looking bars.
        /// </summary>
        public double ZeroAnchorFraction { get; set; }

        /// <summary>
        /// Give every bar a tooltip with its value, range, element count and material names.
        /// </summary>
        public bool ShowToolTips { get; set; }

        public HeatMapGraphOptions()
        {
            UseLogScale = false;
            LogScaleUnavailable = false;
            MinBarWidth = 4;
            TargetTickCount = 8;
            FontSize = 11;
            ZeroAnchorFraction = 0.35;
            ShowToolTips = true;
        }
    }

    /// <summary>
    /// A rounded ("nice") value range: bounds that land on human readable numbers, the step between
    /// them and the number of decimals needed to write them down. Used for both the chart axis and
    /// the cutoff sliders so the two always agree.
    /// </summary>
    public class HeatMapNiceRange
    {
        public double Min { get; internal set; }
        public double Max { get; internal set; }
        public double Step { get; internal set; }
        public int Decimals { get; internal set; }

        /// <summary>
        /// True when the range is so wide or so small that plain decimal notation is unreadable.
        /// </summary>
        public bool UseExponential { get; internal set; }

        public double Span
        {
            get { return Max - Min; }
        }

        /// <summary>
        /// Writes a value using the precision that suits this range.
        /// </summary>
        public string Format(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return "-";

            if (UseExponential)
                return value.ToString("0.###E+0", System.Globalization.CultureInfo.CurrentCulture);

            return value.ToString("N" + Decimals, System.Globalization.CultureInfo.CurrentCulture);
        }
    }
}
