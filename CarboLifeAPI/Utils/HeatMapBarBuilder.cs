/*
 * Carbo Calc Copywrite 2023
 * This static class generates the Heatmapper distribution chart (WPF UIElements) from a CarboGraphResult
 * and stamps the colour that each element will receive in Revit onto that same result.
 *
 * The chart is a distribution profile: the elements that passed the filters are sorted by value and
 * plotted left to right, so the width of a plateau shows how many elements share a value and the
 * shape of the curve shows how the carbon is spread. Where there are more elements than readable
 * pixels the elements are aggregated into bins, never dropped.
 *
 * First a valid set of data needs to be collected using HeatMapCollector,
 * secondly the data can be trimmed with CarboGraphResult.FilterNonVisible / FilterMinMax,
 * finally the UIElements can be extracted here.
 */
using CarboLifeAPI.Data;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Brush = System.Windows.Media.Brush;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace CarboLifeAPI
{
    /// <summary>
    /// Builds the bar chart shown in the Heatmapper and assigns each element the colour it will get in the model.
    /// </summary>
    public static class HeatMapBarBuilder
    {
        /// <summary>
        /// Returns the distribution chart for the given data, and the same data with a colour stamped on
        /// every element. Uses the default render settings.
        /// </summary>
        public static (CarboGraphResult, IList<UIElement>) GetBarGraph(CarboGraphResult _projectData, double _canvasWidth, double _canvasHeight, CarboColourPreset _colourTemplate)
        {
            return GetBarGraph(_projectData, _canvasWidth, _canvasHeight, _colourTemplate, null);
        }

        /// <summary>
        /// Returns the distribution chart for the given data, and the same data with a colour stamped on
        /// every element.
        /// </summary>
        /// <param name="_projectData">The filtered dataset. Elements in validData are plotted and coloured on the gradient, the out-of-bounds sets get the out-of-bounds colours.</param>
        /// <param name="_canvasWidth">Width of the target canvas, in device independent pixels.</param>
        /// <param name="_canvasHeight">Height of the target canvas, in device independent pixels.</param>
        /// <param name="_colourTemplate">The colour preset to interpolate. A default preset is used when null.</param>
        /// <param name="_options">Render settings. Defaults are used when null.</param>
        /// <returns>The same CarboGraphResult that went in (never null when data was supplied) and the chart.</returns>
        public static (CarboGraphResult, IList<UIElement>) GetBarGraph(CarboGraphResult _projectData, double _canvasWidth, double _canvasHeight, CarboColourPreset _colourTemplate, HeatMapGraphOptions _options)
        {
            IList<UIElement> graph = new List<UIElement>();

            if (_projectData == null)
                return (null, graph);

            HeatMapChart chart = new HeatMapChart(
                _projectData,
                _canvasWidth,
                _canvasHeight,
                _colourTemplate ?? new CarboColourPreset(),
                _options ?? new HeatMapGraphOptions());

            try
            {
                graph = chart.Build();
            }
            catch (Exception ex)
            {
                //Never hand back a null dataset and never open a dialog from here: the form keeps its live
                //data and the reason is written on the canvas instead.
                graph = HeatMapChart.BuildErrorNotice(ex.Message, _canvasWidth, _canvasHeight);
            }

            return (_projectData, graph);
        }

        /// <summary>
        /// Rounds a raw data range out to human readable bounds and returns the step between them.
        /// Used for the chart axis and for the cutoff sliders, so that both land on the same numbers.
        /// </summary>
        /// <param name="dataMin">Lowest value in the data.</param>
        /// <param name="dataMax">Highest value in the data.</param>
        /// <param name="targetSteps">Preferred number of steps across the range.</param>
        /// <param name="anchorZero">Force the range to include zero.</param>
        public static HeatMapNiceRange GetNiceRange(double dataMin, double dataMax, int targetSteps, bool anchorZero)
        {
            HeatMapNiceRange result = new HeatMapNiceRange();

            if (!IsFinite(dataMin)) dataMin = 0;
            if (!IsFinite(dataMax)) dataMax = 0;

            if (dataMax < dataMin)
            {
                double swap = dataMin;
                dataMin = dataMax;
                dataMax = swap;
            }

            if (anchorZero)
            {
                if (dataMin > 0) dataMin = 0;
                if (dataMax < 0) dataMax = 0;
            }

            //A range of zero has no scale: give it something sensible to draw on rather than dividing by it.
            if (dataMax - dataMin <= Math.Abs(dataMax) * 1e-9)
            {
                if (dataMax > 0) dataMin = 0;
                else if (dataMax < 0) dataMax = 0;
                else { dataMin = 0; dataMax = 1; }
            }

            int steps = targetSteps < 2 ? 2 : (targetSteps > 20 ? 20 : targetSteps);

            double niceSpan = NiceNumber(dataMax - dataMin, false);
            double step = NiceNumber(niceSpan / steps, true);

            if (!IsFinite(step) || step <= 0)
                step = (dataMax - dataMin) / steps;
            if (!IsFinite(step) || step <= 0)
                step = 1;

            double min = Math.Floor(dataMin / step) * step;
            double max = Math.Ceiling(dataMax / step) * step;

            //Floating point can leave the rounded bound a hair inside the data: never clip the data away.
            if (min > dataMin) min -= step;
            if (max < dataMax) max += step;
            if (max - min <= 0) max = min + step;

            result.Min = min;
            result.Max = max;
            result.Step = step;
            result.Decimals = DecimalsForStep(step);
            result.UseExponential = step < 1e-4 || Math.Max(Math.Abs(min), Math.Abs(max)) >= 1e7;

            return result;
        }

        /// <summary>
        /// Rounds a magnitude to 1, 2, 5 or 10 times a power of ten.
        /// </summary>
        internal static double NiceNumber(double range, bool round)
        {
            if (!IsFinite(range) || range <= 0)
                return 1;

            double exponent = Math.Floor(Math.Log10(range));
            double fraction = range / Math.Pow(10, exponent);
            double niceFraction;

            if (round)
            {
                if (fraction < 1.5) niceFraction = 1;
                else if (fraction < 3) niceFraction = 2;
                else if (fraction < 7) niceFraction = 5;
                else niceFraction = 10;
            }
            else
            {
                if (fraction <= 1) niceFraction = 1;
                else if (fraction <= 2) niceFraction = 2;
                else if (fraction <= 5) niceFraction = 5;
                else niceFraction = 10;
            }

            double result = niceFraction * Math.Pow(10, exponent);
            return IsFinite(result) && result > 0 ? result : 1;
        }

        internal static int DecimalsForStep(double step)
        {
            if (!IsFinite(step) || step <= 0 || step >= 1)
                return 0;

            int decimals = (int)Math.Ceiling(-Math.Log10(step));
            if (decimals < 0) decimals = 0;
            if (decimals > 6) decimals = 6;
            return decimals;
        }

        internal static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    /// <summary>
    /// The value axis of the distribution chart: its bounds, its gridlines and the value the bars grow from.
    /// </summary>
    internal sealed class HeatMapAxis
    {
        private const double LogFloor = 1e-12;

        public double DataMin;
        public double DataMax;

        /// <summary>Lowest value on the axis.</summary>
        public double Min;
        /// <summary>Highest value on the axis.</summary>
        public double Max;
        /// <summary>Step between gridlines, in data units. Not used on a log axis.</summary>
        public double Step;
        /// <summary>The value the bars grow from: zero when zero is on the axis, otherwise the nearest bound.</summary>
        public double Baseline;
        /// <summary>True when zero is not on the axis, so the bars are cut off and must be marked as such.</summary>
        public bool IsTruncated;
        public bool IsLog;

        public List<double> Ticks = new List<double>();
        public HeatMapNiceRange Numbers;

        private double transformedMin;
        private double transformedSpan;

        public double Transform(double value)
        {
            if (!IsLog)
                return value;

            return Math.Log10(value <= 0 ? LogFloor : value);
        }

        /// <summary>
        /// Where a value sits on the axis, 0 at the bottom and 1 at the top.
        /// </summary>
        public double Fraction(double value)
        {
            if (transformedSpan <= 0 || !HeatMapBarBuilder.IsFinite(value))
                return 0;

            double fraction = (Transform(value) - transformedMin) / transformedSpan;
            if (fraction < 0) fraction = 0;
            if (fraction > 1) fraction = 1;
            return fraction;
        }

        public static HeatMapAxis Build(double dataMin, double dataMax, HeatMapGraphOptions options)
        {
            HeatMapAxis axis = new HeatMapAxis();
            axis.DataMin = dataMin;
            axis.DataMax = dataMax;

            int target = options.TargetTickCount < 3 ? 3 : (options.TargetTickCount > 20 ? 20 : options.TargetTickCount);

            //A log axis only exists for strictly positive data. Fall back quietly rather than drawing nonsense.
            bool useLog = options.UseLogScale && dataMin > 0 && dataMax > 0;
            options.LogScaleUnavailable = options.UseLogScale && !useLog;

            if (useLog)
                axis.BuildLogAxis(dataMin, dataMax);
            else
                axis.BuildLinearAxis(dataMin, dataMax, target, options.ZeroAnchorFraction);

            return axis;
        }

        private void BuildLinearAxis(double dataMin, double dataMax, int targetSteps, double zeroAnchorFraction)
        {
            double low = HeatMapBarBuilder.IsFinite(dataMin) ? dataMin : 0;
            double high = HeatMapBarBuilder.IsFinite(dataMax) ? dataMax : 0;

            double anchor = zeroAnchorFraction;
            if (anchor < 0) anchor = 0;
            if (anchor > 1) anchor = 1;

            //Bars read as proportions, so include zero whenever the data comes reasonably close to it.
            //Only a band of values that sits well away from zero gets a truncated axis, and that gets marked.
            bool anchorZero = false;
            if (low > 0 && low <= anchor * high) anchorZero = true;
            else if (high < 0 && high >= anchor * low) anchorZero = true;
            else if (low <= 0 && high >= 0) anchorZero = true;

            Numbers = HeatMapBarBuilder.GetNiceRange(low, high, targetSteps, anchorZero);

            Min = Numbers.Min;
            Max = Numbers.Max;
            Step = Numbers.Step;
            IsLog = false;

            transformedMin = Min;
            transformedSpan = Max - Min;

            //Clamp zero into the axis: the bars grow from zero when it is visible, from the nearest bound otherwise.
            Baseline = Math.Min(Math.Max(0, Min), Max);
            IsTruncated = Min > 0 || Max < 0;

            //Integer stepping: a double accumulator can round back onto itself and never terminate.
            int count = (int)Math.Round((Max - Min) / Step);
            if (count < 1) count = 1;
            if (count > 100) count = 100;

            for (int i = 0; i <= count; i++)
                Ticks.Add(Min + i * Step);
        }

        private void BuildLogAxis(double dataMin, double dataMax)
        {
            double low = Math.Log10(dataMin);
            double high = Math.Log10(dataMax);

            if (high - low < 1e-9)
            {
                low -= 0.5;
                high += 0.5;
            }

            low = Math.Floor(low);
            high = Math.Ceiling(high);
            if (high - low < 1) high = low + 1;

            IsLog = true;
            Min = Math.Pow(10, low);
            Max = Math.Pow(10, high);
            Step = 0;

            transformedMin = low;
            transformedSpan = high - low;

            //A log axis can never show zero, so the bars always start at the bottom of the plot.
            Baseline = Min;
            IsTruncated = true;

            Numbers = new HeatMapNiceRange();
            Numbers.Min = Min;
            Numbers.Max = Max;
            Numbers.Step = Min;
            Numbers.Decimals = HeatMapBarBuilder.DecimalsForStep(Min);
            Numbers.UseExponential = Min < 1e-4 || Max >= 1e7;

            int decades = (int)Math.Round(high - low);
            if (decades < 1) decades = 1;
            if (decades > 30) decades = 30;

            bool includeMinorTicks = decades <= 4;

            for (int k = 0; k <= decades; k++)
            {
                double decade = Math.Pow(10, low + k);
                Ticks.Add(decade);

                if (includeMinorTicks && k < decades)
                {
                    Ticks.Add(decade * 2);
                    Ticks.Add(decade * 5);
                }
            }

            Ticks.Sort();
        }

        /// <summary>
        /// Writes an axis value with the precision that suits this axis.
        /// </summary>
        public string FormatTick(double value)
        {
            if (!IsLog)
                return Numbers.Format(value);

            //Every decade on a log axis needs its own precision.
            double magnitude = Math.Abs(value);
            if (magnitude >= 1e6 || (magnitude > 0 && magnitude < 1e-4))
                return value.ToString("0.###E+0", CultureInfo.CurrentCulture);

            int decimals = magnitude >= 1 ? 0 : (int)Math.Ceiling(-Math.Log10(magnitude));
            if (decimals < 0) decimals = 0;
            if (decimals > 6) decimals = 6;
            return value.ToString("N" + decimals, CultureInfo.CurrentCulture);
        }
    }

    /// <summary>
    /// One bar of the chart. Holds the elements that were aggregated into it so the tooltip can report them.
    /// </summary>
    internal sealed class HeatMapBin
    {
        /// <summary>The value that sets the bar height: the one furthest from the baseline.</summary>
        public double Value;
        public double LowValue;
        public double HighValue;
        public int Count;
        public List<string> Names = new List<string>();
        public int HiddenNameCount;
        public System.Drawing.Color Colour;
    }

    /// <summary>
    /// Draws the distribution chart. One instance per render, so nothing is carried between calls.
    /// </summary>
    internal sealed class HeatMapChart
    {
        private const string FontName = "Segoe UI";

        private static readonly Brush AxisBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)));
        private static readonly Brush GridBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE2)));
        private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)));
        private static readonly Brush MutedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));
        private static readonly Brush BarEdgeBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x45, 0, 0, 0)));
        private static readonly Brush HoverBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)));

        private readonly CarboGraphResult data;
        private readonly double canvasWidth;
        private readonly double canvasHeight;
        private readonly CarboColourPreset colours;
        private readonly HeatMapGraphOptions options;
        private readonly List<UIElement> graph = new List<UIElement>();

        private double fontSize;
        private double plotLeft;
        private double plotTop;
        private double plotWidth;
        private double plotHeight;

        private HeatMapAxis axis;
        private List<HeatMapBin> bins;
        private List<TextBlock> tickLabels;
        private int plottedCount;
        private int skippedCount;
        private bool showTitles;
        private bool showLabels;

        public HeatMapChart(CarboGraphResult _data, double _canvasWidth, double _canvasHeight, CarboColourPreset _colours, HeatMapGraphOptions _options)
        {
            data = _data;
            canvasWidth = _canvasWidth;
            canvasHeight = _canvasHeight;
            colours = _colours;
            options = _options;
        }

        public IList<UIElement> Build()
        {
            fontSize = Clamp(options.FontSize <= 0 ? 11 : options.FontSize, 8, 16);

            //The out-of-bounds elements are coloured regardless of whether anything can be drawn.
            ColourOutOfBounds();

            List<CarboValues> values = CollectPlottableValues();
            plottedCount = values.Count;

            if (values.Count == 0)
            {
                if (CanDraw())
                    DrawCentredMessage("No elements fall within the selected range");
                return graph;
            }

            axis = HeatMapAxis.Build(values[0].Value, values[values.Count - 1].Value, options);

            //Colouring is what Revit consumes, so it happens even when the canvas is too small to draw on.
            ColourValidData(values);

            if (!CanDraw() || !Layout())
                return graph;

            bins = BuildBins(values);

            DrawGridLines();
            DrawBars();
            DrawAxisLines();
            DrawValueAxisLabels();
            DrawElementAxisLabels();
            DrawNotes();

            return graph;
        }

        /// <summary>
        /// A single line of text on an otherwise empty canvas, used when the chart itself cannot be drawn.
        /// </summary>
        public static IList<UIElement> BuildErrorNotice(string message, double width, double height)
        {
            List<UIElement> result = new List<UIElement>();

            if (!(width > 0) || !(height > 0))
                return result;

            TextBlock label = new TextBlock();
            label.Text = "The chart could not be drawn: " + message;
            label.FontFamily = new FontFamily(FontName);
            label.FontSize = 11;
            label.Foreground = MutedBrush;
            label.TextWrapping = TextWrapping.Wrap;
            label.MaxWidth = Math.Max(40, width - 20);
            label.Measure(new Size(label.MaxWidth, double.PositiveInfinity));

            Canvas.SetLeft(label, 10);
            Canvas.SetTop(label, Math.Max(4, (height - label.DesiredSize.Height) / 2));
            result.Add(label);

            return result;
        }

        //**************************************************************************
        //Data
        //**************************************************************************

        /// <summary>
        /// The elements to plot, sorted by value. Values that are not finite (a zero volume element
        /// divided out to infinity, for instance) are counted and left out rather than poisoning the scale.
        /// </summary>
        private List<CarboValues> CollectPlottableValues()
        {
            List<CarboValues> result = new List<CarboValues>();

            if (data.validData == null)
                return result;

            foreach (CarboValues cv in data.validData)
            {
                if (HeatMapBarBuilder.IsFinite(cv.Value))
                    result.Add(cv);
                else
                    skippedCount++;
            }

            result.Sort(delegate (CarboValues a, CarboValues b) { return a.Value.CompareTo(b.Value); });
            return result;
        }

        /// <summary>
        /// Gives every plotted element the colour that matches its own value, so that the gradient means
        /// intensity rather than rank order. The colours are cached per percent because the blend is
        /// quantised to whole percentages anyway.
        /// </summary>
        private void ColourValidData(List<CarboValues> sortedValues)
        {
            System.Drawing.Color[] cache = new System.Drawing.Color[101];
            bool[] cached = new bool[101];

            foreach (CarboValues cv in sortedValues)
            {
                System.Drawing.Color colour = ColourForValue(cv.Value, cache, cached);
                cv.r = colour.R;
                cv.g = colour.G;
                cv.b = colour.B;
            }
        }

        private void ColourOutOfBounds()
        {
            if (data.outOfBoundsMaxData != null)
            {
                foreach (CarboValues cv in data.outOfBoundsMaxData)
                {
                    cv.r = colours.outmax.r;
                    cv.g = colours.outmax.g;
                    cv.b = colours.outmax.b;
                }
            }

            if (data.outOfBoundsMinData != null)
            {
                foreach (CarboValues cv in data.outOfBoundsMinData)
                {
                    cv.r = colours.outmin.r;
                    cv.g = colours.outmin.g;
                    cv.b = colours.outmin.b;
                }
            }
        }

        /// <summary>
        /// The position of a value inside the plotted data range, 0 for the lowest and 1 for the highest.
        /// Follows the axis transform, so a log axis also gives a log colour ramp.
        /// </summary>
        private double ColourFraction(double value)
        {
            double low = axis.Transform(axis.DataMin);
            double high = axis.Transform(axis.DataMax);
            double span = high - low;

            //Every element shares one value: there is no high or low, so use the middle of the ramp.
            if (span <= 0 || !HeatMapBarBuilder.IsFinite(span))
                return 0.5;

            double fraction = (axis.Transform(value) - low) / span;
            return Clamp(fraction, 0, 1);
        }

        private System.Drawing.Color ColourForValue(double value, System.Drawing.Color[] cache, bool[] cached)
        {
            int percent = (int)Math.Round(ColourFraction(value) * 100);
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            if (!cached[percent])
            {
                System.Drawing.Color minColour = System.Drawing.Color.FromArgb(colours.min.a, colours.min.r, colours.min.g, colours.min.b);
                System.Drawing.Color midColour = System.Drawing.Color.FromArgb(colours.mid.a, colours.mid.r, colours.mid.g, colours.mid.b);
                System.Drawing.Color maxColour = System.Drawing.Color.FromArgb(colours.max.a, colours.max.r, colours.max.g, colours.max.b);

                //Utils counts down from the max colour, hence the inversion.
                cache[percent] = Utils.GetBlendedColor(100 - percent, minColour, midColour, maxColour);
                cached[percent] = true;
            }

            return cache[percent];
        }

        /// <summary>
        /// Groups the sorted elements into as many bars as there are readable pixels. Each bar keeps the
        /// number of elements it covers, so a value shared by many elements now occupies the width it deserves.
        /// </summary>
        private List<HeatMapBin> BuildBins(List<CarboValues> sortedValues)
        {
            double minBarWidth = options.MinBarWidth <= 0 ? 4 : options.MinBarWidth;
            int maxBars = (int)Math.Floor(plotWidth / minBarWidth);
            if (maxBars < 1) maxBars = 1;

            int barCount = Math.Min(sortedValues.Count, maxBars);
            if (barCount < 1) barCount = 1;

            List<HeatMapBin> result = new List<HeatMapBin>(barCount);
            System.Drawing.Color[] cache = new System.Drawing.Color[101];
            bool[] cached = new bool[101];

            for (int i = 0; i < barCount; i++)
            {
                int from = (int)((long)i * sortedValues.Count / barCount);
                int to = (int)((long)(i + 1) * sortedValues.Count / barCount);

                if (to <= from) to = from + 1;
                if (to > sortedValues.Count) to = sortedValues.Count;
                if (from >= sortedValues.Count) break;

                HeatMapBin bin = new HeatMapBin();
                bin.LowValue = sortedValues[from].Value;
                bin.HighValue = sortedValues[to - 1].Value;
                bin.Count = to - from;

                //Show the end of the bin that is furthest from the baseline, so the envelope of the
                //distribution stays visible for negative data too.
                bin.Value = Math.Abs(bin.HighValue - axis.Baseline) >= Math.Abs(bin.LowValue - axis.Baseline)
                    ? bin.HighValue
                    : bin.LowValue;

                HashSet<string> seen = new HashSet<string>();
                for (int k = from; k < to; k++)
                {
                    string name = sortedValues[k].ValueName;
                    if (string.IsNullOrEmpty(name) || !seen.Add(name))
                        continue;

                    if (bin.Names.Count < 4)
                        bin.Names.Add(name);
                    else
                        bin.HiddenNameCount++;
                }

                bin.Colour = ColourForValue(bin.Value, cache, cached);
                result.Add(bin);
            }

            return result;
        }

        //**************************************************************************
        //Layout
        //**************************************************************************

        private bool CanDraw()
        {
            return canvasWidth > 0 && canvasHeight > 0;
        }

        /// <summary>
        /// Measures the axis labels and derives the plot margins from them, so a long number can never
        /// overflow into the plot. Drops the axis titles, and then the labels, when the canvas is too small
        /// to carry them.
        /// </summary>
        private bool Layout()
        {
            for (int detail = 2; detail >= 0; detail--)
            {
                showTitles = detail >= 2;
                showLabels = detail >= 1;

                double left = 4;
                double right = 8;
                double top = 6;
                double bottom = 4;

                if (showLabels)
                {
                    tickLabels = new List<TextBlock>(axis.Ticks.Count);
                    double widest = 0;
                    foreach (double tick in axis.Ticks)
                    {
                        TextBlock label = CreateLabel(axis.FormatTick(tick), TextBrush);
                        tickLabels.Add(label);
                        if (label.DesiredSize.Width > widest)
                            widest = label.DesiredSize.Width;
                    }

                    double lineHeight = tickLabels.Count > 0 ? tickLabels[0].DesiredSize.Height : fontSize * 1.4;

                    left = 4 + widest + 6;
                    bottom = 4 + lineHeight + 4;
                    top = 4 + lineHeight / 2;

                    if (showTitles)
                    {
                        left += lineHeight + 2;
                        bottom += lineHeight;
                    }
                }
                else
                {
                    tickLabels = new List<TextBlock>();
                }

                plotLeft = left;
                plotTop = top;
                plotWidth = canvasWidth - left - right;
                plotHeight = canvasHeight - top - bottom;

                if (plotWidth >= 40 && plotHeight >= 30)
                    return true;
            }

            return false;
        }

        private double YFor(double value)
        {
            double y = plotTop + plotHeight * (1 - axis.Fraction(value));
            return Clamp(y, plotTop, plotTop + plotHeight);
        }

        //**************************************************************************
        //Drawing
        //**************************************************************************

        private void DrawGridLines()
        {
            //Ticks run bottom to top, so a label placed later has a smaller y than the one before it.
            //Keeping the occupied bands instead of a single edge makes the collision test independent
            //of that direction, which matters on a log axis where the minor ticks bunch up.
            List<double> occupiedTop = new List<double>();
            List<double> occupiedBottom = new List<double>();

            for (int i = 0; i < axis.Ticks.Count; i++)
            {
                double tick = axis.Ticks[i];
                if (tick < axis.Min || tick > axis.Max)
                    continue;

                double y = YFor(tick);

                Line gridLine = new Line();
                gridLine.X1 = plotLeft;
                gridLine.X2 = plotLeft + plotWidth;
                gridLine.Y1 = y;
                gridLine.Y2 = y;
                gridLine.Stroke = GridBrush;
                gridLine.StrokeThickness = 1;
                graph.Add(gridLine);

                if (!showLabels || i >= tickLabels.Count)
                    continue;

                TextBlock label = tickLabels[i];
                double labelTop = y - label.DesiredSize.Height / 2;
                double labelBottom = labelTop + label.DesiredSize.Height;

                //Skip a label rather than let it overlap one that is already placed.
                bool collides = false;
                for (int k = 0; k < occupiedTop.Count; k++)
                {
                    if (labelTop < occupiedBottom[k] + 1 && labelBottom > occupiedTop[k] - 1)
                    {
                        collides = true;
                        break;
                    }
                }
                if (collides)
                    continue;

                occupiedTop.Add(labelTop);
                occupiedBottom.Add(labelBottom);

                Canvas.SetLeft(label, plotLeft - 6 - label.DesiredSize.Width);
                Canvas.SetTop(label, labelTop);
                graph.Add(label);
            }
        }

        private void DrawBars()
        {
            if (bins == null || bins.Count == 0)
                return;

            double slot = plotWidth / bins.Count;
            double gap = slot >= 6 ? Math.Min(2, slot * 0.15) : 0;
            double barWidth = Math.Max(1, slot - gap);
            double baselineY = YFor(axis.Baseline);

            for (int i = 0; i < bins.Count; i++)
            {
                HeatMapBin bin = bins[i];

                double valueY = YFor(bin.Value);
                double top = Math.Min(baselineY, valueY);
                double barHeight = Math.Abs(baselineY - valueY);

                //Keep a bar that sits on the baseline visible instead of drawing nothing.
                if (barHeight < 1) barHeight = 1;
                if (top + barHeight > plotTop + plotHeight)
                    top = plotTop + plotHeight - barHeight;

                SolidColorBrush fill = new SolidColorBrush(Color.FromArgb(255, bin.Colour.R, bin.Colour.G, bin.Colour.B));
                fill.Freeze();

                Rectangle bar = new Rectangle();
                bar.Width = barWidth;
                bar.Height = barHeight;
                bar.Fill = fill;

                //An outline on a bar only a few pixels wide swallows the colour it is meant to frame.
                if (barWidth >= 4)
                {
                    bar.Stroke = BarEdgeBrush;
                    bar.StrokeThickness = 0.5;
                }

                Canvas.SetLeft(bar, plotLeft + i * slot);
                Canvas.SetTop(bar, top);

                if (options.ShowToolTips)
                    bar.ToolTip = BuildToolTip(bin);

                AttachHover(bar);
                graph.Add(bar);
            }
        }

        /// <summary>
        /// Highlights a bar under the cursor. This replaces the rotated labels that used to be stamped
        /// next to every bar.
        /// </summary>
        private void AttachHover(Rectangle bar)
        {
            Brush originalStroke = bar.Stroke;
            double originalThickness = bar.StrokeThickness;

            bar.MouseEnter += delegate (object sender, System.Windows.Input.MouseEventArgs e)
            {
                bar.Stroke = HoverBrush;
                bar.StrokeThickness = 1.5;
                Panel.SetZIndex(bar, 10);
            };

            bar.MouseLeave += delegate (object sender, System.Windows.Input.MouseEventArgs e)
            {
                bar.Stroke = originalStroke;
                bar.StrokeThickness = originalThickness;
                Panel.SetZIndex(bar, 0);
            };
        }

        private string BuildToolTip(HeatMapBin bin)
        {
            string unit = string.IsNullOrEmpty(data.Unit) ? "" : " " + data.Unit;
            System.Text.StringBuilder tip = new System.Text.StringBuilder();

            tip.Append(string.IsNullOrEmpty(data.ValueName) ? "Value" : data.ValueName);
            tip.Append(": ");
            tip.Append(FormatPrecise(bin.Value));
            tip.Append(unit);

            if (bin.Count > 1 && bin.HighValue > bin.LowValue)
            {
                tip.AppendLine();
                tip.Append("Range: ");
                tip.Append(FormatPrecise(bin.LowValue));
                tip.Append(" - ");
                tip.Append(FormatPrecise(bin.HighValue));
                tip.Append(unit);
            }

            tip.AppendLine();
            tip.Append(bin.Count.ToString("N0", CultureInfo.CurrentCulture));
            tip.Append(bin.Count == 1 ? " element" : " elements");

            if (bin.Names.Count > 0)
            {
                tip.AppendLine();
                tip.Append(string.Join(", ", bin.Names.ToArray()));
                if (bin.HiddenNameCount > 0)
                {
                    tip.Append(" +");
                    tip.Append(bin.HiddenNameCount);
                    tip.Append(" more");
                }
            }

            return tip.ToString();
        }

        private void DrawAxisLines()
        {
            double baselineY = YFor(axis.Baseline);

            //Value axis
            Line valueAxis = new Line();
            valueAxis.X1 = plotLeft;
            valueAxis.X2 = plotLeft;
            valueAxis.Y1 = plotTop;
            valueAxis.Y2 = plotTop + plotHeight;
            valueAxis.Stroke = AxisBrush;
            valueAxis.StrokeThickness = 1;
            graph.Add(valueAxis);

            //Baseline. Dashed when it is not zero, so a truncated bar is never read as a full one.
            Line baseLine = new Line();
            baseLine.X1 = plotLeft;
            baseLine.X2 = plotLeft + plotWidth;
            baseLine.Y1 = baselineY;
            baseLine.Y2 = baselineY;
            baseLine.Stroke = AxisBrush;
            baseLine.StrokeThickness = 1;
            if (axis.IsTruncated)
                baseLine.StrokeDashArray = new DoubleCollection(new double[] { 3, 2 });
            graph.Add(baseLine);

            //When the axis is not truncated the baseline above is zero by definition, so bars that hang
            //below it read as negative without any further marking.
            if (axis.IsTruncated)
                DrawAxisBreak(baselineY);
        }

        /// <summary>
        /// The two slashes that say the value axis does not start at zero.
        /// </summary>
        private void DrawAxisBreak(double baselineY)
        {
            int direction = baselineY > plotTop + plotHeight / 2 ? -1 : 1;

            for (int i = 0; i < 2; i++)
            {
                double centre = baselineY + direction * (8 + i * 5);
                if (centre < plotTop || centre > plotTop + plotHeight)
                    continue;

                Line slash = new Line();
                slash.X1 = plotLeft - 4;
                slash.X2 = plotLeft + 4;
                slash.Y1 = centre + 3;
                slash.Y2 = centre - 3;
                slash.Stroke = AxisBrush;
                slash.StrokeThickness = 1;
                graph.Add(slash);
            }
        }

        private void DrawValueAxisLabels()
        {
            if (!showTitles)
                return;

            string caption = string.IsNullOrEmpty(data.ValueName) ? "Value" : data.ValueName;
            if (!string.IsNullOrEmpty(data.Unit))
                caption += " [" + data.Unit + "]";
            if (axis.IsLog)
                caption += " - log";

            TextBlock title = CreateLabel(caption, MutedBrush);
            double textWidth = title.DesiredSize.Width;

            //Rotated a quarter turn the block is arranged at its transformed size, so it is placed by the
            //top left corner of the upright box it now occupies.
            title.LayoutTransform = new RotateTransform(-90);
            Canvas.SetLeft(title, 2);
            Canvas.SetTop(title, plotTop + Math.Max(0, (plotHeight - textWidth) / 2));
            graph.Add(title);
        }

        /// <summary>
        /// Labels the element axis with element counts, which is the quantity the bar widths now carry.
        /// </summary>
        private void DrawElementAxisLabels()
        {
            if (!showLabels)
                return;

            double axisY = plotTop + plotHeight;
            double[] fractions = new double[] { 0, 0.25, 0.5, 0.75, 1 };
            double lastRight = double.NegativeInfinity;

            foreach (double fraction in fractions)
            {
                double x = plotLeft + plotWidth * fraction;
                int count = (int)Math.Round(plottedCount * fraction);

                TextBlock label = CreateLabel(count.ToString("N0", CultureInfo.CurrentCulture), MutedBrush);
                double left = x - label.DesiredSize.Width / 2;

                //Keep the first and last label inside the plot.
                if (left < plotLeft) left = plotLeft;
                if (left + label.DesiredSize.Width > plotLeft + plotWidth)
                    left = plotLeft + plotWidth - label.DesiredSize.Width;

                if (left < lastRight + 6)
                    continue;
                lastRight = left + label.DesiredSize.Width;

                Line tick = new Line();
                tick.X1 = x;
                tick.X2 = x;
                tick.Y1 = axisY;
                tick.Y2 = axisY + 3;
                tick.Stroke = AxisBrush;
                tick.StrokeThickness = 1;
                graph.Add(tick);

                Canvas.SetLeft(label, left);
                Canvas.SetTop(label, axisY + 4);
                graph.Add(label);
            }

            if (!showTitles)
                return;

            TextBlock title = CreateLabel("Elements, sorted by value", MutedBrush);
            Canvas.SetLeft(title, plotLeft + Math.Max(0, (plotWidth - title.DesiredSize.Width) / 2));
            Canvas.SetTop(title, axisY + 4 + title.DesiredSize.Height);
            graph.Add(title);
        }

        /// <summary>
        /// A quiet note in the corner when values had to be left out of the plot.
        /// </summary>
        private void DrawNotes()
        {
            if (skippedCount <= 0)
                return;

            TextBlock note = CreateLabel(skippedCount.ToString("N0", CultureInfo.CurrentCulture) + " value(s) not plotted (no valid number)", MutedBrush);
            Canvas.SetLeft(note, Math.Max(plotLeft, plotLeft + plotWidth - note.DesiredSize.Width));
            Canvas.SetTop(note, plotTop);
            graph.Add(note);
        }

        private void DrawCentredMessage(string message)
        {
            TextBlock label = CreateLabel(message, MutedBrush);
            Canvas.SetLeft(label, Math.Max(4, (canvasWidth - label.DesiredSize.Width) / 2));
            Canvas.SetTop(label, Math.Max(4, (canvasHeight - label.DesiredSize.Height) / 2));
            graph.Add(label);
        }

        //**************************************************************************
        //Helpers
        //**************************************************************************

        /// <summary>
        /// Creates a label and measures it, so the caller can lay out against its real size.
        /// </summary>
        private TextBlock CreateLabel(string text, Brush foreground)
        {
            TextBlock label = new TextBlock();
            label.Text = text ?? "";
            label.FontFamily = new FontFamily(FontName);
            label.FontSize = fontSize;
            label.Foreground = foreground;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return label;
        }

        private string FormatPrecise(double value)
        {
            if (!HeatMapBarBuilder.IsFinite(value))
                return "-";

            return value.ToString("G5", CultureInfo.CurrentCulture);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value)) return min;
            return value < min ? min : (value > max ? max : value);
        }

        private static Brush Frozen(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }
    }
}
