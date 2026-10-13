using System;
using System.Collections;
using System.ComponentModel;
using Avalonia.Collections;
using Avalonia.Controls;

namespace CodeplugBuilder.Mac
{
    /// <summary>
    /// Click-to-sort for a DataGrid, like the Windows ListSorter: the clicked column ascending, then descending, then the
    /// list's own order. Kept across refills (call <see cref="Show"/> instead of setting ItemsSource). Columns sort by their
    /// SortMemberPath (set it to a number property for numeric order) or their binding; empty values go last both ways.
    /// </summary>
    sealed class GridSorter
    {
        readonly DataGrid grid;
        string path;
        bool descending;
        IEnumerable items;

        /// <summary>Raised after the sort changes (on, reversed or off).</summary>
        public event EventHandler Changed;

        /// <summary>True while the grid shows a sorted order rather than the list's own.</summary>
        public bool Active => path != null;

        public GridSorter(DataGrid grid)
        {
            this.grid = grid;
            grid.CanUserSortColumns = true;
            grid.Sorting += (s, e) => { e.Handled = true; Click(e.Column); };
        }

        static string PathOf(DataGridColumn c)
        {
            if (!string.IsNullOrEmpty(c.SortMemberPath)) return c.SortMemberPath;
            return ((c as DataGridBoundColumn)?.Binding as Avalonia.Data.Binding)?.Path;
        }

        void Click(DataGridColumn c)
        {
            if (!c.CanUserSort) return;
            string p = PathOf(c);
            if (string.IsNullOrEmpty(p)) return;
            if (p != path) { path = p; descending = false; }
            else if (!descending) descending = true;
            else path = null;
            Show(items);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Shows <paramref name="rows"/> in the grid, sorted the way the user last asked.</summary>
        public void Show(IEnumerable rows)
        {
            items = rows;
            if (rows == null) { grid.ItemsSource = null; return; }
            var view = new DataGridCollectionView(rows);
            if (path != null)
                view.SortDescriptions.Add(DataGridSortDescription.FromPath(path, descending ? ListSortDirection.Descending : ListSortDirection.Ascending, new EmptyLast(descending)));
            grid.ItemsSource = view;
        }

        /// <summary>Compares cell values: numbers as numbers, text ignoring case, empty (null, "", NaN) last whichever the direction.</summary>
        sealed class EmptyLast : IComparer
        {
            readonly bool descending;
            public EmptyLast(bool descending) { this.descending = descending; }

            static bool Empty(object o) { return o == null || (o is string s && s.Trim().Length == 0) || (o is double d && double.IsNaN(d)); }

            public int Compare(object a, object b)
            {
                bool ea = Empty(a), eb = Empty(b);
                // The view flips the result for descending, so flip it here first to keep empties at the bottom.
                if (ea || eb) return ea && eb ? 0 : (ea ? 1 : -1) * (descending ? -1 : 1);
                if (a.GetType() == b.GetType() && a is IComparable ca) return ca.CompareTo(b);
                return string.Compare(a.ToString(), b.ToString(), StringComparison.CurrentCultureIgnoreCase);
            }
        }
    }
}
