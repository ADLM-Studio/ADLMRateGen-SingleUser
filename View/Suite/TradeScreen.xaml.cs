using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace ADLMRateGen.View.Suite
{
    /// <summary>
    /// The trade screen every item of work shares: title, Add Custom Rate, the
    /// overhead / profit / find / sort bar, and the one table. Opening a row
    /// runs the trade view model's ShowDetailsCommand, which raises the rate's
    /// composition in the window's popup host exactly as before.
    /// </summary>
    public partial class TradeScreen : UserControl
    {
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(TradeScreen), new PropertyMetadata(""));

        public static readonly DependencyProperty ItemsProperty =
            DependencyProperty.Register(nameof(Items), typeof(IEnumerable), typeof(TradeScreen),
                new PropertyMetadata(null, (d, e) => ((TradeScreen)d).OnItemsChanged()));

        /// <summary>What an empty trade says, when the default is not the truth for it.</summary>
        public static readonly DependencyProperty EmptyTextProperty =
            DependencyProperty.Register(nameof(EmptyText), typeof(string), typeof(TradeScreen), new PropertyMetadata(null));

        public string? EmptyText
        {
            get => (string?)GetValue(EmptyTextProperty);
            set => SetValue(EmptyTextProperty, value);
        }

        /// <summary>
        /// Carbon & Others: the table shows the trade a rate comes from, its total cost,
        /// its upfront carbon per unit and the coverage of that figure, and the
        /// overhead and profit boxes (which carbon does not use) step aside.
        /// </summary>
        public static readonly DependencyProperty IsCarbonProperty =
            DependencyProperty.Register(nameof(IsCarbon), typeof(bool), typeof(TradeScreen),
                new PropertyMetadata(false, (d, _) => ((TradeScreen)d).ApplyMode()));

        public bool IsCarbon
        {
            get => (bool)GetValue(IsCarbonProperty);
            set => SetValue(IsCarbonProperty, value);
        }

        /// <summary>The line under the title, when the trade's default is not the truth for it.</summary>
        public static readonly DependencyProperty LedeProperty =
            DependencyProperty.Register(nameof(Lede), typeof(string), typeof(TradeScreen),
                new PropertyMetadata(null, (d, e) => { if (e.NewValue is string s) ((TradeScreen)d).LedeText.Text = s; }));

        public string? Lede
        {
            get => (string?)GetValue(LedeProperty);
            set => SetValue(LedeProperty, value);
        }

        private void ApplyMode()
        {
            if (ColTrade == null) return;
            var carbon = IsCarbon ? Visibility.Visible : Visibility.Collapsed;
            var cost = IsCarbon ? Visibility.Collapsed : Visibility.Visible;
            ColTrade.Visibility = carbon; ColCarbon.Visibility = carbon; ColCoverage.Visibility = carbon;
            ColNet.Visibility = cost; ColProfit.Visibility = cost; ColOverhead.Visibility = cost;
            OhBox.Visibility = cost; PrBox.Visibility = cost;
        }

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public IEnumerable? Items
        {
            get => (IEnumerable?)GetValue(ItemsProperty);
            set => SetValue(ItemsProperty, value);
        }

        private ICollectionView? _view;

        public TradeScreen()
        {
            InitializeComponent();
            Loaded += (_, __) => { ApplySort(); UpdateEmpty(); };
            SizeChanged += (_, e) => Reflow(e.NewSize.Width);
        }

        /// <summary>
        /// Narrow: overhead and profit keep the first line and find + sort take
        /// the second, full width, so nothing is squeezed below readable.
        /// </summary>
        private void Reflow(double width)
        {
            bool narrow = width < 760;
            Bar.ColumnDefinitions[2].MinWidth = narrow ? 0 : 180;
            Grid.SetRow(FindBox, narrow ? 1 : 0);
            Grid.SetColumn(FindBox, narrow ? 0 : 2);
            Grid.SetColumnSpan(FindBox, narrow ? 3 : 1);
            FindBox.Margin = narrow ? new Thickness(0, 10, 10, 0) : new Thickness(0, 0, 10, 0);
            Grid.SetRow(SortBox, narrow ? 1 : 0);
            SortBox.Margin = narrow ? new Thickness(0, 10, 0, 0) : new Thickness(0);
        }

        private ICollectionView? View =>
            Items == null ? null : Items as ICollectionView ?? CollectionViewSource.GetDefaultView(Items);

        private void OnItemsChanged()
        {
            if (_view is INotifyCollectionChanged old) old.CollectionChanged -= OnViewChanged;
            _view = View;
            if (_view is INotifyCollectionChanged now) now.CollectionChanged += OnViewChanged;
            ApplySort();
            UpdateEmpty();
        }

        private void OnViewChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmpty();

        // The three orders a QS actually asks for. "Order in the bill" clears
        // sorting, which returns the library's own sequence.
        private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplySort();

        private void ApplySort()
        {
            var view = View;
            if (view == null || SortBox == null) return;
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                switch (SortBox.SelectedIndex)
                {
                    case 1: view.SortDescriptions.Add(new SortDescription(IsCarbon ? "CarbonTotal" : "TotalCost", ListSortDirection.Descending)); break;
                    case 2: view.SortDescriptions.Add(new SortDescription("Description", ListSortDirection.Ascending)); break;
                }
            }
        }

        private void UpdateEmpty()
        {
            var view = View;
            bool empty = view == null || view.IsEmpty;
            None.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (!empty) return;
            None.Text = string.IsNullOrWhiteSpace(Find.Text)
                ? EmptyText ?? "Nothing in this trade yet. Check the library for updates from the top bar and the rates will come down."
                : "No rate in this trade matches that.";
        }

        private void Open(object? item)
        {
            if (item == null) return;
            var cmd = DataContext?.GetType().GetProperty("ShowDetailsCommand")?.GetValue(DataContext) as ICommand;
            if (cmd != null && cmd.CanExecute(item)) cmd.Execute(item);
        }

        private void Table_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Only a click on a row opens it; the header and the scrollbar do not.
            var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row != null) Open(row.Item);
        }

        private void Table_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Space) return;
            Open(Table.SelectedItem ?? Table.CurrentItem);
            e.Handled = true;
        }

        private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d != null && d is not T)
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            return d as T;
        }
    }
}
