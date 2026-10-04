using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hearthstone_Deck_Tracker.Utility.Extensions;
using HdtApi = Hearthstone_Deck_Tracker.API;
using User32 = Hearthstone_Deck_Tracker.User32;

namespace DiscardOdds
{
	/// <summary>
	/// One overlay widget: "Targets left in deck: N of M (X% next draw)", the deck's targets, and hit/miss lines.
	/// Built in code (no XAML) so the project compiles with the plain .NET SDK.
	/// Drag: unlock via the Plugins menu. While unlocked, a low-level mouse hook (HDT's own User32.MouseInput,
	/// the same approach as the DrawPool plugin) moves the widget; position is saved as fractions of the overlay size.
	/// </summary>
	public class PayoffWidget : Border
	{
		private readonly TextBlock _title;
		private readonly TextBlock _main;
		private readonly TextBlock _sub;
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
			Padding = new Thickness(8, 5, 8, 6);
			IsHitTestVisible = false;
			SnapsToDevicePixels = true;

			_title = new TextBlock { Text = "DISCARD ODDS · target cards · if you play it: hit / miss", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0x9C, 0xE0)), FontWeight = FontWeights.SemiBold };
			_main = new TextBlock { Text = "Targets left in deck: –", FontSize = 15, Foreground = Brushes.White, FontWeight = FontWeights.Bold };
			_sub = new TextBlock { Text = "", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), TextWrapping = TextWrapping.Wrap, MaxWidth = 430 };
			var stack = new StackPanel();
			stack.Children.Add(_title);
			stack.Children.Add(_main);
			stack.Children.Add(_sub);
			Child = stack;
			Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.7 };
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

		public void SetText(string main, string sub)
		{
			_main.Text = main;
			_sub.Text = sub;
		}

		public void SetUnlocked(bool unlocked)
		{
			if(Unlocked == unlocked) return;
			Unlocked = unlocked;
			BorderBrush = unlocked ? Brushes.Gold : new SolidColorBrush(Color.FromArgb(0xFF, 0x8E, 0x5B, 0xC9));
			BorderThickness = new Thickness(unlocked ? 2.5 : 1.5);
			_title.Text = unlocked ? "DISCARD ODDS · drag me, then lock via Plugins menu" : "DISCARD ODDS · target cards · if you play it: hit / miss";
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
