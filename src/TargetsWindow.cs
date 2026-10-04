using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DiscardOdds
{
	/// <summary>
	/// "Choose target cards" window: the active deck's cards with checkboxes. Built in code (no XAML).
	/// Save writes the deck's list into targets.json; "Use preset / auto" removes the deck's own list.
	/// </summary>
	public class TargetsWindow : Window
	{
		private readonly TargetConfig _config;
		private readonly string _deckId;
		private readonly string _deckName;
		private readonly Action _onSaved;
		private readonly List<(CheckBox box, DeckCardInfo card)> _rows = new List<(CheckBox, DeckCardInfo)>();
		private readonly ComboBox _presets;

		public TargetsWindow(TargetConfig config, string deckId, string deckName, List<DeckCardInfo> cards, ISet<string> current, string currentSource, Action onSaved)
		{
			_config = config;
			_deckId = deckId;
			_deckName = deckName;
			_onSaved = onSaved;
			Title = "Discard Odds: target cards";
			Width = 420;
			Height = 640;
			WindowStartupLocation = WindowStartupLocation.CenterScreen;
			ResizeMode = ResizeMode.CanResize;

			var root = new DockPanel { Margin = new Thickness(10) };

			var header = new TextBlock
			{
				Text = $"Deck: {deckName}\nTick the cards you want odds for (payoffs, combo pieces, win conditions).\nCurrently: {currentSource}.",
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0, 0, 0, 8)
			};
			DockPanel.SetDock(header, Dock.Top);
			root.Children.Add(header);

			var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
			_presets = new ComboBox { MinWidth = 220, ItemsSource = config.Presets.Select(p => p.Name).ToList() };
			if(config.Presets.Count > 0) _presets.SelectedIndex = 0;
			var applyPreset = new Button { Content = "Tick preset cards", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 2, 6, 2) };
			applyPreset.Click += (s, e) =>
			{
				var p = config.Presets.FirstOrDefault(x => x.Name == _presets.SelectedItem as string);
				if(p == null) return;
				var ids = new HashSet<string>(p.Targets.Select(t => t.Id));
				foreach(var r in _rows) r.box.IsChecked = ids.Contains(r.card.Id);
			};
			presetRow.Children.Add(_presets);
			presetRow.Children.Add(applyPreset);
			DockPanel.SetDock(presetRow, Dock.Top);
			root.Children.Add(presetRow);

			var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
			Button B(string text, RoutedEventHandler click)
			{
				var b = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3) };
				b.Click += click;
				buttons.Children.Add(b);
				return b;
			}
			B("Clear all", (s, e) => { foreach(var r in _rows) r.box.IsChecked = false; });
			B("Use preset / auto", (s, e) => SaveAndClose(removeOwnList: true)).ToolTip = "Forget this deck's own list; an auto-matching preset (if any) is used instead.";
			B("Save", (s, e) => SaveAndClose(removeOwnList: false)).IsDefault = true;
			B("Cancel", (s, e) => Close()).IsCancel = true;
			DockPanel.SetDock(buttons, Dock.Bottom);
			root.Children.Add(buttons);

			var list = new StackPanel();
			if(cards.Count == 0)
				list.Children.Add(new TextBlock { Text = "No active deck in HDT. Select a deck first.", Foreground = Brushes.Gray });
			foreach(var c in cards)
			{
				var box = new CheckBox
				{
					Content = $"({c.Cost}) {c.Name}  x{c.Copies}",
					IsChecked = current.Contains(c.Id),
					Margin = new Thickness(0, 2, 0, 2),
					ToolTip = c.Id
				};
				_rows.Add((box, c));
				list.Children.Add(box);
			}
			root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
			Content = root;
		}

		private void SaveAndClose(bool removeOwnList)
		{
			try
			{
				if(removeOwnList)
					_config.RemoveDeck(_deckId, _deckName);
				else
					_config.SetDeckTargets(_deckId, _deckName,
						_rows.Where(r => r.box.IsChecked == true).Select(r => new TargetCard { Id = r.card.Id, Name = r.card.Name }));
				_onSaved?.Invoke();
				Close();
			}
			catch(Exception ex)
			{
				MessageBox.Show(this, "Could not save targets.json: " + ex.Message, "Discard Odds");
			}
		}
	}
}
