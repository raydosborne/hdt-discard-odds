using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Media;
using Hearthstone_Deck_Tracker.Utility.Extensions;
using HdtApi = Hearthstone_Deck_Tracker.API;
using User32 = Hearthstone_Deck_Tracker.User32;

namespace DiscardOdds
{
	/// <summary>One widget line: either an odds row (name, hit % green, miss % red) or plain text.</summary>
	public sealed class WidgetRow
	{
		public string Name;            // card name (gold) at the start of the line
		public bool NameIsLabel;       // Name is a plain label (e.g. "Next draw"), not a card: white instead of gold
		public double? Hit;            // odds row when set
		public bool Approx;
		public string Detail;          // dim second line, only with "Show details"
		public string DiscardRule;     // "highest" / "lowest": DiscardNames are colored by this rule
		public List<(string name, bool target)> DiscardNames;
		public string Text;            // plain row when set
		public Brush TextColor;
		public bool Bold;
		public bool Small;
		public bool DetailOnly;        // only shown with "Show details"
		public string Suffix;          // small dim text after the numbers on the same line (e.g. the targets list)
	}

	public sealed class WidgetContent
	{
		public WidgetRow Header;
		public List<WidgetRow> Rows = new List<WidgetRow>();
	}

	/// <summary>Fixed colors, the same everywhere so they are learned at a glance.</summary>
	public static class WidgetColors
	{
		private static SolidColorBrush B(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
		public static readonly SolidColorBrush Hit = B(0x5B, 0xE3, 0x7D);      // hit %: bold green
		public static readonly SolidColorBrush Miss = B(0xFF, 0x6B, 0x6B);     // miss %: red
		public static readonly SolidColorBrush Highest = B(0xFF, 0xA0, 0x40);  // card a highest-Cost discard would hit: orange
		public static readonly SolidColorBrush Lowest = B(0x4F, 0xC3, 0xF7);   // card a lowest-Cost discard would hit: cyan
		public static readonly SolidColorBrush CardName = B(0xFF, 0xD5, 0x4F);  // card name at the start of a line: gold
		public static readonly SolidColorBrush Text = B(0xF0, 0xF0, 0xF0);
		public static readonly SolidColorBrush Light = B(0xC8, 0xC8, 0xC8);
		public static readonly SolidColorBrush Dim = B(0x9A, 0xA0, 0xA6);
		public static readonly SolidColorBrush Note = B(0xB3, 0x9D, 0xDB);     // rare-event note (lavender, kept apart from the gold names)
	}

	/// <summary>
	/// One compact overlay widget: header "Next draw  hit / miss · targets left", the lethal line on your turn, one line
	/// per odds card in hand, and the opening one-drop line. Reasons are hidden unless "Show details" is on.
	/// Built in code (no XAML) so the project compiles with the plain .NET SDK.
	/// Drag: unlock via the Plugins menu. While unlocked, a low-level mouse hook (HDT's own User32.MouseInput,
	/// the same approach as the DrawPool plugin) moves the widget; position is saved as fractions of the overlay size.
	/// </summary>
	public class PayoffWidget : Border
	{
		private const double MaxW = 400;
		private readonly TextBlock _title;
		private readonly StackPanel _header;
		private readonly StackPanel _rows;
		private readonly TextBlock _lethal;
		private readonly PluginSettings _settings;
		private User32.MouseInput _mouse;
		private bool _dragging;
		private Point _grabOffset;

		public bool Unlocked { get; private set; }

		public PayoffWidget(PluginSettings settings)
		{
			_settings = settings;
			Name = "DiscardOddsPayoffWidget";
			Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x1B, 0x14, 0x24));
			BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x8E, 0x5B, 0xC9));
			BorderThickness = new Thickness(1.5);
			CornerRadius = new CornerRadius(6);
			Padding = new Thickness(7, 3, 7, 4);
			IsHitTestVisible = false;
			SnapsToDevicePixels = true;

			// The title only shows while unlocked (drag hint), to keep the widget small.
			_title = new TextBlock { Text = "DISCARD ODDS · drag me, then lock via Plugins menu", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0x9C, 0xE0)), FontWeight = FontWeights.SemiBold, Visibility = Visibility.Collapsed };
			_header = new StackPanel();
			_lethal = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = MaxW, Visibility = Visibility.Collapsed };
			_rows = new StackPanel();
			var stack = new StackPanel();
			stack.Children.Add(_title);
			stack.Children.Add(_header);
			stack.Children.Add(_lethal);
			stack.Children.Add(_rows);
			Child = stack;
			Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.7 };
		}

		/// <summary>Replaces the widget's lines (the lethal line is set separately).</summary>
		public void SetContent(WidgetContent c)
		{
			_header.Children.Clear();
			_rows.Children.Clear();
			if(c.Header != null) _header.Children.Add(RowBlock(c.Header, 14));
			foreach(var r in c.Rows)
			{
				if(r.DetailOnly && !_settings.ShowDetails) continue;
				_rows.Children.Add(RowBlock(r, 13));
			}
			if(_settings.ShowDetails)
			{
				var legend = new TextBlock { FontSize = 10, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = MaxW };
				legend.Inlines.Add(new Run("card") { Foreground = WidgetColors.CardName });
				legend.Inlines.Add(new Run(" · ") { Foreground = WidgetColors.Dim });
				legend.Inlines.Add(new Run("hit %") { Foreground = WidgetColors.Hit, FontWeight = FontWeights.Bold });
				legend.Inlines.Add(new Run(" / ") { Foreground = WidgetColors.Dim });
				legend.Inlines.Add(new Run("miss %") { Foreground = WidgetColors.Miss });
				legend.Inlines.Add(new Run(" · → discards: ") { Foreground = WidgetColors.Dim });
				legend.Inlines.Add(new Run("highest Cost") { Foreground = WidgetColors.Highest });
				legend.Inlines.Add(new Run(" · ") { Foreground = WidgetColors.Dim });
				legend.Inlines.Add(new Run("lowest Cost") { Foreground = WidgetColors.Lowest });
				legend.Inlines.Add(new Run(" · bold = target · ≈ approximate") { Foreground = WidgetColors.Dim });
				_rows.Children.Add(legend);
			}
		}

		private TextBlock RowBlock(WidgetRow r, double size)
		{
			var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = MaxW, FontSize = size };
			if(r.Text != null)
			{
				if(r.Name != null)
					tb.Inlines.Add(new Run(r.Name + " ") { Foreground = r.NameIsLabel ? WidgetColors.Text : WidgetColors.CardName, FontWeight = FontWeights.SemiBold, FontSize = r.Small ? 11.5 : size });
				tb.Inlines.Add(new Run(r.Text)
				{
					Foreground = r.TextColor ?? WidgetColors.Light,
					FontWeight = r.Bold ? FontWeights.Bold : FontWeights.Normal,
					FontSize = r.Small ? 11.5 : size
				});
			}
			else
			{
				var hit = Math.Max(0, Math.Min(1, r.Hit ?? 0));
				tb.Inlines.Add(new Run((r.Name ?? "?") + "  ") { Foreground = r.NameIsLabel ? WidgetColors.Text : WidgetColors.CardName, FontWeight = FontWeights.SemiBold });
				tb.Inlines.Add(new Run(OddsEngine.Pct(hit)) { Foreground = WidgetColors.Hit, FontWeight = FontWeights.Bold, FontSize = size + 1.5 });
				tb.Inlines.Add(new Run(" / ") { Foreground = WidgetColors.Dim });
				tb.Inlines.Add(new Run(OddsEngine.Pct(1 - hit)) { Foreground = WidgetColors.Miss, FontWeight = FontWeights.SemiBold });
				if(r.Approx) tb.Inlines.Add(new Run(" ≈") { Foreground = WidgetColors.Dim });
				if(r.DiscardNames != null && r.DiscardNames.Count > 0 && r.DiscardRule != null)
				{
					var color = r.DiscardRule == "lowest" ? WidgetColors.Lowest : WidgetColors.Highest;
					tb.Inlines.Add(new Run("  → ") { Foreground = WidgetColors.Dim });
					for(var i = 0; i < r.DiscardNames.Count; i++)
					{
						if(i > 0) tb.Inlines.Add(new Run(" / ") { Foreground = WidgetColors.Dim });
						tb.Inlines.Add(new Run(r.DiscardNames[i].name) { Foreground = color, FontWeight = r.DiscardNames[i].target ? FontWeights.Bold : FontWeights.Normal });
					}
				}
				if(!string.IsNullOrEmpty(r.Suffix))
					tb.Inlines.Add(new Run("   " + r.Suffix) { Foreground = WidgetColors.Light, FontSize = 11.5, FontWeight = FontWeights.Normal });
			}
			if(_settings.ShowDetails && !string.IsNullOrEmpty(r.Detail))
				tb.Inlines.Add(new Run("\n   " + r.Detail) { FontSize = 10.5, Foreground = WidgetColors.Dim, FontWeight = FontWeights.Normal });
			return tb;
		}

		public void Attach()
		{
			var canvas = HdtApi.Core.OverlayCanvas;
			if(!canvas.Children.Contains(this))
				canvas.Children.Add(this);
			canvas.SizeChanged += CanvasOnSizeChanged;
			ApplyPosition();
		}

		public void Detach()
		{
			SetUnlocked(false);
			try
			{
				var canvas = HdtApi.Core.OverlayCanvas;
				canvas.SizeChanged -= CanvasOnSizeChanged;
				canvas.Children.Remove(this);
			}
			catch { }
		}

		private void CanvasOnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyPosition();

		public void ApplyPosition()
		{
			var canvas = HdtApi.Core.OverlayCanvas;
			var w = canvas.ActualWidth > 0 ? canvas.ActualWidth : canvas.Width;
			var h = canvas.ActualHeight > 0 ? canvas.ActualHeight : canvas.Height;
			if(double.IsNaN(w) || double.IsNaN(h) || w <= 0 || h <= 0) return;
			Canvas.SetLeft(this, Clamp(_settings.WidgetLeftFraction, 0, 0.98) * w);
			Canvas.SetTop(this, Clamp(_settings.WidgetTopFraction, 0, 0.98) * h);
		}

		/// <summary>Lethal-check line (null hides it): bold, green when lethal, red when short. Detail only with "Show details".</summary>
		public void SetLethal(string line, string detail, bool lethal)
		{
			if(line == null) { _lethal.Visibility = Visibility.Collapsed; return; }
			_lethal.Inlines.Clear();
			_lethal.Inlines.Add(new Run(line) { FontWeight = FontWeights.Bold, Foreground = lethal ? WidgetColors.Hit : WidgetColors.Miss });
			if(_settings.ShowDetails && !string.IsNullOrEmpty(detail))
				_lethal.Inlines.Add(new Run("\n   " + detail) { FontSize = 10.5, FontWeight = FontWeights.Normal, Foreground = WidgetColors.Dim });
			_lethal.Visibility = Visibility.Visible;
		}

		public void SetUnlocked(bool unlocked)
		{
			if(Unlocked == unlocked) return;
			Unlocked = unlocked;
			BorderBrush = unlocked ? Brushes.Gold : new SolidColorBrush(Color.FromArgb(0xFF, 0x8E, 0x5B, 0xC9));
			BorderThickness = new Thickness(unlocked ? 2.5 : 1.5);
			_title.Visibility = unlocked ? Visibility.Visible : Visibility.Collapsed;
			try
			{
				// Lets HDT know this element wants mouse input while unlocked (verified API: OverlayExtensions).
				OverlayExtensions.SetIsOverlayHitTestVisible(this, unlocked);
			}
			catch(Exception ex)
			{
				ProbeLog.Line("UI", "SetIsOverlayHitTestVisible failed: " + ex.Message);
			}
			if(unlocked)
			{
				try
				{
					_mouse = new User32.MouseInput();
					_mouse.LmbDown += OnLmbDown;
					_mouse.LmbUp += OnLmbUp;
					_mouse.MouseMoved += OnMouseMoved;
				}
				catch(Exception ex)
				{
					ProbeLog.Line("UI", "MouseInput hook failed: " + ex.Message);
				}
			}
			else
			{
				_dragging = false;
				if(_mouse != null)
				{
					_mouse.LmbDown -= OnLmbDown;
					_mouse.LmbUp -= OnLmbUp;
					_mouse.MouseMoved -= OnMouseMoved;
					try { _mouse.Dispose(); } catch { }
					_mouse = null;
				}
				_settings.Save();
			}
		}

		private Point MouseOnCanvas()
		{
			var p = User32.GetMousePos();
			return HdtApi.Core.OverlayCanvas.PointFromScreen(new Point(p.X, p.Y));
		}

		private void OnLmbDown(object sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
		{
			try
			{
				var p = MouseOnCanvas();
				var left = Canvas.GetLeft(this);
				var top = Canvas.GetTop(this);
				if(double.IsNaN(left)) left = 0;
				if(double.IsNaN(top)) top = 0;
				if(p.X >= left && p.X <= left + ActualWidth && p.Y >= top && p.Y <= top + ActualHeight)
				{
					_dragging = true;
					_grabOffset = new Point(p.X - left, p.Y - top);
				}
			}
			catch { }
		}));

		private void OnMouseMoved(object sender, EventArgs e)
		{
			if(!_dragging) return;
			Dispatcher.BeginInvoke(new Action(() =>
			{
				try
				{
					var canvas = HdtApi.Core.OverlayCanvas;
					var p = MouseOnCanvas();
					var x = Clamp(p.X - _grabOffset.X, 0, Math.Max(0, canvas.ActualWidth - ActualWidth));
					var y = Clamp(p.Y - _grabOffset.Y, 0, Math.Max(0, canvas.ActualHeight - ActualHeight));
					Canvas.SetLeft(this, x);
					Canvas.SetTop(this, y);
				}
				catch { }
			}));
		}

		private void OnLmbUp(object sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
		{
			if(!_dragging) return;
			_dragging = false;
			try
			{
				var canvas = HdtApi.Core.OverlayCanvas;
				if(canvas.ActualWidth > 0 && canvas.ActualHeight > 0)
				{
					_settings.WidgetLeftFraction = Canvas.GetLeft(this) / canvas.ActualWidth;
					_settings.WidgetTopFraction = Canvas.GetTop(this) / canvas.ActualHeight;
					_settings.WidgetPositionSaved = true;
					_settings.Save();
				}
			}
			catch { }
		}));

		private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
	}
}
