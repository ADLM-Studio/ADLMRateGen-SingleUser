#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ADLMRateGen.Helpers
{
    /// <summary>
    /// One keyboard shortcut: a gesture, what it does, and when it is available.
    /// </summary>
    public sealed class KeyboardShortcut
    {
        public Key Key { get; set; }
        public ModifierKeys Modifiers { get; set; }
        public string Group { get; set; }
        public string Description { get; set; }
        public Action Run { get; set; }
        /// <summary>Null means always available. An unavailable shortcut is also left off the sheet.</summary>
        public Func<bool> IsAvailable { get; set; }
        /// <summary>Shown on the sheet instead of the gesture.</summary>
        public string DisplayGesture { get; set; }
        /// <summary>False keeps an alias (e.g. Ctrl+/ for F1) working without a second row on the sheet.</summary>
        public bool ShowOnSheet { get; set; } = true;
        /// <summary>A row the sheet shows but the map never handles, for keys a product already handles itself (e.g. Esc).</summary>
        public bool IsNote { get; set; }

        public string Gesture => DisplayGesture ?? KeyboardShortcutMap.Format(Key, Modifiers);
    }

    /// <summary>
    /// The shortcut table for one window or dock pane. The same gestures mean the
    /// same thing in every ADLM product:
    ///   F1 / Ctrl+/      this sheet
    ///   Ctrl+1 .. 9      the sidebar sections, top to bottom
    ///   Ctrl+S           save          Ctrl+F   find / search
    ///   F5               refresh/sync  Ctrl+,   settings
    ///   Ctrl+Shift+L     light/dark    Ctrl+B   collapse sidebar
    ///
    /// Handled on PreviewKeyDown of the host so it works whatever child has focus.
    /// Plain (unmodified) letter keys are never taken while a text field has focus.
    /// Written in C# 7.3 so the one file compiles in every ADLM WPF project; copy
    /// it, do not fork it.
    /// </summary>
    public sealed class KeyboardShortcutMap
    {
        private readonly List<KeyboardShortcut> _items = new List<KeyboardShortcut>();
        private readonly string _productName;
        private UIElement _lastHost;
        private Window _sheet;

        public KeyboardShortcutMap(string productName)
        {
            _productName = string.IsNullOrWhiteSpace(productName) ? "ADLM" : productName;
            // Every product opens the sheet on the same two gestures.
            _items.Add(new KeyboardShortcut
            {
                Key = Key.F1, Group = "General", Description = "Show keyboard shortcuts",
                Run = ToggleSheet, DisplayGesture = "F1  or  Ctrl+/"
            });
            _items.Add(new KeyboardShortcut
            {
                Key = Key.OemQuestion, Modifiers = ModifierKeys.Control, Group = "General",
                Description = "Show keyboard shortcuts", Run = ToggleSheet, ShowOnSheet = false
            });
        }

        public IReadOnlyList<KeyboardShortcut> Items => _items;

        public KeyboardShortcutMap Add(Key key, ModifierKeys modifiers, string group, string description,
                                       Action run, Func<bool> isAvailable = null)
        {
            _items.Add(new KeyboardShortcut
            {
                Key = key, Modifiers = modifiers, Group = group, Description = description,
                Run = run, IsAvailable = isAvailable
            });
            return this;
        }

        /// <summary>Adds a row built by hand (e.g. one whose label follows what is on screen).</summary>
        public KeyboardShortcutMap Add(KeyboardShortcut shortcut)
        {
            if (shortcut != null) _items.Add(shortcut);
            return this;
        }

        /// <summary>Binds a gesture to an ICommand; available whenever the command can execute.</summary>
        public KeyboardShortcutMap AddCommand(Key key, ModifierKeys modifiers, string group, string description,
                                              Func<ICommand> command)
        {
            return Add(key, modifiers, group, description,
                () => { var c = command(); if (c != null && c.CanExecute(null)) c.Execute(null); },
                () => { var c = command(); return c != null && c.CanExecute(null); });
        }

        /// <summary>Lists a gesture the product already handles elsewhere, so the sheet is complete.</summary>
        public KeyboardShortcutMap AddNote(string gesture, string group, string description)
        {
            _items.Add(new KeyboardShortcut { Key = Key.None, DisplayGesture = gesture, Group = group, Description = description, IsNote = true });
            return this;
        }

        /// <summary>Ctrl+1, Ctrl+2 ... in order. Pass null for a slot to skip its number.</summary>
        public KeyboardShortcutMap AddSections(string group, params KeyboardShortcut[] sections)
        {
            for (int i = 0; i < sections.Length && i < 9; i++)
            {
                var s = sections[i];
                if (s == null) continue;
                s.Key = Key.D1 + i;
                s.Modifiers = ModifierKeys.Control;
                s.Group = s.Group ?? group;
                _items.Add(s);
            }
            return this;
        }

        /// <summary>A section entry for <see cref="AddSections"/>.</summary>
        public static KeyboardShortcut Section(string name, Action run, Func<bool> isAvailable = null)
        {
            return new KeyboardShortcut { Description = "Go to " + name, Run = run, IsAvailable = isAvailable };
        }

        /// <summary>Hooks the map to a window, page or dock pane. Safe to call for several hosts.</summary>
        public void AttachTo(UIElement host)
        {
            if (host == null) return;
            if (_lastHost == null) _lastHost = host;
            host.PreviewKeyDown += (s, e) =>
            {
                _lastHost = host;
                if (e.Handled) return;
                if (TryHandle(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers, IsTyping()))
                    e.Handled = true;
            };
        }

        /// <summary>Runs the first available shortcut matching the gesture. Separate from the event so it can be tested.</summary>
        public bool TryHandle(Key key, ModifierKeys mods, bool typing)
        {
            key = Normalize(key);
            foreach (var s in _items)
            {
                if (s.Key != key || s.Modifiers != mods || s.Run == null) continue;
                if (typing && !IsSafeWhileTyping(s.Key, s.Modifiers)) continue;
                if (s.IsAvailable != null && !Safe(s.IsAvailable)) continue;
                try { s.Run(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Shortcut " + s.Gesture + " failed: " + ex); }
                return true;
            }
            return false;
        }

        private static bool IsTyping()
        {
            var f = Keyboard.FocusedElement;
            if (f is TextBoxBase || f is PasswordBox) return true;
            var cb = f as ComboBox;
            return cb != null && cb.IsEditable;
        }

        /// <summary>Function keys and Ctrl/Alt chords never type a character, so they are safe in a text field.</summary>
        public static bool IsSafeWhileTyping(Key key, ModifierKeys mods)
        {
            if (key >= Key.F1 && key <= Key.F24) return true;
            return (mods & (ModifierKeys.Control | ModifierKeys.Alt)) != 0;
        }

        /// <summary>Keypad digits behave like the top-row digits.</summary>
        public static Key Normalize(Key key)
        {
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return Key.D0 + (key - Key.NumPad0);
            return key;
        }

        public static string Format(Key key, ModifierKeys mods)
        {
            var parts = new List<string>();
            if ((mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
            if ((mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
            parts.Add(KeyName(key));
            return string.Join("+", parts);
        }

        public static string KeyName(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9) return ((int)(key - Key.D0)).ToString();
            switch (key)
            {
                case Key.OemQuestion: return "/";
                case Key.OemComma: return ",";
                case Key.OemPeriod: return ".";
                case Key.OemPlus: return "+";
                case Key.OemMinus: return "-";
                case Key.Escape: return "Esc";
                case Key.Return: return "Enter";
                case Key.Delete: return "Del";
                default: return key.ToString();
            }
        }

        private static bool Safe(Func<bool> f)
        {
            try { return f(); } catch { return false; }
        }

        /// <summary>What the sheet lists: shortcuts available right now, grouped, in registration order.</summary>
        public List<KeyValuePair<string, List<KeyboardShortcut>>> SheetGroups()
        {
            return _items.Where(s => s.ShowOnSheet && (s.IsNote || (s.Run != null && (s.IsAvailable == null || Safe(s.IsAvailable)))))
                         .GroupBy(s => s.Group ?? "General")
                         .Select(g => new KeyValuePair<string, List<KeyboardShortcut>>(g.Key, g.ToList()))
                         .ToList();
        }

        private void ToggleSheet()
        {
            if (_sheet != null) { _sheet.Close(); return; }
            ShowSheet(_lastHost);
        }

        /// <summary>Opens the sheet over the host, coloured from the host so it follows light/dark.</summary>
        public void ShowSheet(UIElement host)
        {
            if (_sheet != null) { _sheet.Activate(); return; }
            Window owner = host != null ? Window.GetWindow(host) : null;

            Brush bg = FindBrush(host, "CardBg", "CardBackground", "SurfaceBrush", "ContentBg")
                       ?? (owner != null ? owner.Background : null) ?? Brushes.White;
            var sbg = bg as SolidColorBrush;
            Color c = sbg != null ? sbg.Color : Colors.White;
            bool dark = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 140;
            var fg = new SolidColorBrush(dark ? Color.FromRgb(0xF3, 0xF4, 0xF6) : Color.FromRgb(0x11, 0x18, 0x27));
            var muted = new SolidColorBrush(dark ? Color.FromRgb(0x9C, 0xA3, 0xAF) : Color.FromRgb(0x6B, 0x72, 0x80));
            var chipBg = new SolidColorBrush(dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0));
            var chipBorder = new SolidColorBrush(dark ? Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x33, 0, 0, 0));

            var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            root.Children.Add(new TextBlock
            {
                Text = "Keyboard shortcuts", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = fg
            });
            root.Children.Add(new TextBlock
            {
                Text = _productName + "  ·  F1 or Esc closes this", FontSize = 11, Foreground = muted,
                Margin = new Thickness(0, 2, 0, 8)
            });

            foreach (var group in SheetGroups())
            {
                root.Children.Add(new TextBlock
                {
                    Text = group.Key.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.SemiBold,
                    Foreground = muted, Margin = new Thickness(0, 12, 0, 4)
                });
                foreach (var s in group.Value)
                {
                    var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(new TextBlock
                    {
                        Text = s.Description, FontSize = 13, Foreground = fg,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0)
                    });
                    var chip = new Border
                    {
                        Background = chipBg, BorderBrush = chipBorder, BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2, 8, 2),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Child = new TextBlock { Text = s.Gesture, FontSize = 12, Foreground = fg, FontFamily = new FontFamily("Consolas") }
                    };
                    Grid.SetColumn(chip, 1);
                    row.Children.Add(chip);
                    root.Children.Add(row);
                }
            }

            var sheet = new Window
            {
                Title = _productName + " - Keyboard shortcuts",
                Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                Background = bg,
                SizeToContent = SizeToContent.WidthAndHeight,
                MaxHeight = SystemParameters.WorkArea.Height * 0.85,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false,
                Topmost = owner == null,
                WindowStartupLocation = owner != null && owner.IsVisible
                    ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
            };
            if (owner != null && owner.IsVisible) sheet.Owner = owner;
            sheet.PreviewKeyDown += (s, e) =>
            {
                Key k = e.Key == Key.System ? e.SystemKey : e.Key;
                if (k == Key.Escape || k == Key.F1 || (k == Key.OemQuestion && Keyboard.Modifiers == ModifierKeys.Control))
                {
                    sheet.Close();
                    e.Handled = true;
                }
            };
            sheet.Closed += (s, e) =>
            {
                _sheet = null;
                if (owner != null && owner.IsVisible) owner.Activate();
            };
            _sheet = sheet;
            sheet.Show();
        }

        private static Brush FindBrush(UIElement host, params string[] keys)
        {
            var fe = host as FrameworkElement;
            foreach (var k in keys)
            {
                object r = fe != null ? fe.TryFindResource(k)
                         : (Application.Current != null ? Application.Current.TryFindResource(k) : null);
                var b = r as Brush;
                if (b != null) return b;
            }
            return null;
        }
    }
}
