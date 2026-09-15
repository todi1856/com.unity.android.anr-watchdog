using System;
using System.Collections;
using UnityEngine.UIElements;

namespace Unity.Android
{
    /// <summary>
    /// A column table built out of a ListView, with a header above it. MultiColumnListView would do
    /// this for us, but it only exists from Unity 2022.2 on and this package targets 2021.3.
    /// <para>
    /// Rows are a flex row of labels, created once and rebound as they scroll, so a report with a
    /// thousand stack frames costs a screenful of elements rather than a thousand.
    /// </para>
    /// </summary>
    class AnrTable
    {
        public readonly struct Column
        {
            public readonly string title;

            /// <summary>Fixed width in pixels, or 0 to share what the fixed columns leave over.</summary>
            public readonly float width;

            public Column(string title, float width = 0)
            {
                this.title = title;
                this.width = width;
            }
        }

        const float k_RowHeight = 20;

        public readonly VisualElement root = new VisualElement();
        public readonly ListView list = new ListView();

        /// <summary>Fills one row. Called for every row that scrolls into view.</summary>
        public Action<int, Label[]> bindRow;

        public event Action selectionChanged;

        readonly Column[] m_Columns;

        public AnrTable(params Column[] columns)
        {
            m_Columns = columns;

            root.AddToClassList("table");
            root.Add(BuildHeader());

            list.fixedItemHeight = k_RowHeight;
            list.showAlternatingRowBackgrounds = AlternatingRowBackground.All;
            list.makeItem = MakeRow;
            list.bindItem = Bind;
            list.AddToClassList("table-list");
            list.onSelectionChange += _ => selectionChanged?.Invoke();

            root.Add(list);
        }

        public IList itemsSource
        {
            get => list.itemsSource;
            set => list.itemsSource = value;
        }

        public int selectedIndex => list.selectedIndex;

        public void Rebuild() => list.Rebuild();

        public void RefreshItems() => list.RefreshItems();

        VisualElement BuildHeader()
        {
            var header = new VisualElement();
            header.AddToClassList("table-header");

            foreach (var column in m_Columns)
            {
                var label = new Label(column.title);
                label.AddToClassList("table-cell");
                label.AddToClassList("table-header-cell");
                ApplyWidth(label, column);
                header.Add(label);
            }

            return header;
        }

        VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("table-row");

            var cells = new Label[m_Columns.Length];
            for (var i = 0; i < m_Columns.Length; i++)
            {
                cells[i] = new Label();
                cells[i].AddToClassList("table-cell");
                ApplyWidth(cells[i], m_Columns[i]);
                row.Add(cells[i]);
            }

            // Kept on the row so binding does not have to walk or allocate.
            row.userData = cells;
            return row;
        }

        void Bind(VisualElement element, int index)
        {
            if (element.userData is Label[] cells)
                bindRow?.Invoke(index, cells);
        }

        static void ApplyWidth(VisualElement element, Column column)
        {
            if (column.width > 0)
            {
                element.style.width = column.width;
                element.style.flexShrink = 0;
            }
            else
            {
                element.style.flexGrow = 1;
                element.style.flexBasis = 0;
            }
        }
    }
}
