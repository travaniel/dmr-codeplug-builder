using System;
using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CodeplugBuilder.App
{
    /// <summary>
    /// Click-to-sort for a details ListView (<see cref="Ui.Sortable"/>): the clicked column ascending, then descending,
    /// then off. The sorted column's header shows ▲ or ▼.
    /// </summary>
    sealed class ListSorter : IComparer
    {
        readonly ListView list;
        readonly Action unsorted;
        readonly string[] headers;
        int column = -1;
        bool descending;
        static readonly Regex Leading = new Regex(@"^\s*-?\d+(\.\d+)?");

        /// <summary>Raised after the sort changes (on, reversed or off).</summary>
        public event EventHandler Changed;

        /// <summary>True while the list is being re-sorted: checkbox lists can raise ItemChecked then; ignore those.</summary>
        public bool Sorting { get; private set; }

        /// <summary>True while the list shows a sorted order rather than its own.</summary>
        public bool Active => column >= 0;

        public ListSorter(ListView list, Action unsorted)
        {
            this.list = list;
            this.unsorted = unsorted;
            headers = new string[list.Columns.Count];
            for (int i = 0; i < headers.Length; i++) headers[i] = list.Columns[i].Text;
            list.ColumnClick += (s, e) => Click(e.Column);
        }

        void Click(int c)
        {
            if (c != column) { column = c; descending = false; }
            else if (!descending) descending = true;
            else column = -1;
            for (int i = 0; i < headers.Length && i < list.Columns.Count; i++)
            {
                var col = list.Columns[i];
                col.Text = headers[i] + (i == column ? (descending ? " ▼" : " ▲") : "");
                // Room for the arrow: a narrow column would cut the header to "Output MHz...".
                int needed = TextRenderer.MeasureText(col.Text, list.Font).Width + Ui.S(16);
                if (i == column && col.Width < needed) col.Width = needed;
            }
            if (column >= 0) Apply();
            else
            {
                list.ListViewItemSorter = null;
                unsorted?.Invoke();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Sorts the list again (after it was refilled); nothing when no column is sorted.</summary>
        public void Apply()
        {
            if (column < 0) return;
            Sorting = true;
            try
            {
                if (list.ListViewItemSorter != this) list.ListViewItemSorter = this; // setting it sorts
                else list.Sort();
            }
            finally { Sorting = false; }
            if (list.SelectedItems.Count > 0) list.SelectedItems[0].EnsureVisible();
        }

        public int Compare(object x, object y)
        {
            string a = Cell((ListViewItem)x), b = Cell((ListViewItem)y);
            // Empty cells last, whichever way.
            if (a.Length == 0 || b.Length == 0) return a.Length == 0 && b.Length == 0 ? 0 : a.Length == 0 ? 1 : -1;
            int r;
            var ma = Leading.Match(a);
            var mb = Leading.Match(b);
            if (ma.Success && mb.Success &&
                double.TryParse(ma.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double na) &&
                double.TryParse(mb.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double nb) && na != nb)
                r = na.CompareTo(nb);
            else
                r = string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
            return descending ? -r : r;
        }

        string Cell(ListViewItem item)
        {
            return column < item.SubItems.Count ? (item.SubItems[column].Text ?? "").Trim() : "";
        }
    }
}
