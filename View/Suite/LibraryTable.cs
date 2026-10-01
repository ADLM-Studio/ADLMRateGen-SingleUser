using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ADLMRateGen.View.Suite
{
    /// <summary>
    /// What the material and labour libraries share with the trade screens:
    /// the three sorts, opening a row, the narrow-window reflow and the empty
    /// state. One place, so the two libraries cannot drift apart.
    /// </summary>
    public static class LibraryTable
    {
        public static void Sort(ICollectionView? view, int index, string priceProp, string nameProp)
        {
            if (view == null) return;
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                if (index == 1) view.SortDescriptions.Add(new SortDescription(priceProp, ListSortDirection.Descending));
                if (index == 2) view.SortDescriptions.Add(new SortDescription(nameProp, ListSortDirection.Ascending));
            }
        }

        public static void Run(object? dataContext, string commandName, object? arg)
        {
            if (arg == null) return;
            var cmd = dataContext?.GetType().GetProperty(commandName)?.GetValue(dataContext) as ICommand;
            if (cmd != null && cmd.CanExecute(arg)) cmd.Execute(arg);
        }

        /// <summary>A click on a row (not the header, not the scrollbar) edits it.</summary>
        public static void OpenRowUnder(MouseButtonEventArgs e, DataGrid grid, object? dataContext, string commandName)
        {
            DependencyObject? d = e.OriginalSource as DependencyObject;
            while (d != null && d is not DataGridRow && d != grid)
                d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            if (d is DataGridRow row) Run(dataContext, commandName, row.Item);
        }

        /// <summary>Narrow: search takes the whole first line, the rest share the second.</summary>
        public static void Reflow(double width, Grid bar, FrameworkElement find, FrameworkElement category,
                                  FrameworkElement sort, FrameworkElement update)
        {
            bool narrow = width < 820;
            bar.ColumnDefinitions[0].MinWidth = narrow ? 0 : 180;
            Grid.SetColumnSpan(find, narrow ? 4 : 1);
            find.Margin = narrow ? new Thickness(0, 0, 0, 10) : new Thickness(0, 0, 10, 0);
            foreach (var (el, col) in new[] { (category, 0), (sort, 1), (update, 2) })
            {
                Grid.SetRow(el, narrow ? 1 : 0);
                Grid.SetColumn(el, narrow ? col : col + 1);
            }
            update.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        }

        public static void WatchEmpty(ICollectionView? view, TextBlock none, string what)
        {
            void Update()
            {
                bool empty = view == null || view.IsEmpty;
                none.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
                none.Text = view != null && view.Filter != null
                    ? $"No {what} item matches that. Clear the search or choose All."
                    : $"The {what} library is empty. Update prices brings ADLM's library down to this PC.";
            }
            if (view is INotifyCollectionChanged n) n.CollectionChanged += (_, __) => Update();
            Update();
        }
    }
}
